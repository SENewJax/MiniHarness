using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.Model.OpenAI
{
    public sealed class ModelOptions
    {
        public string Protocol { get; set; } = "Responses";
        public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 120;
        public int MaxRetries { get; set; } = 2;

        public void Validate()
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("Model:BaseUrl must be an absolute HTTP(S) URL.");
            if (TimeoutSeconds < 1 || MaxRetries < 0 || MaxRetries > 5 || string.IsNullOrWhiteSpace(Model))
                throw new ArgumentException("Invalid model configuration. Set Model:Model to your upstream model ID.");
            if (Protocol != "Responses" && Protocol != "ChatCompletions") throw new ArgumentException("Unknown model protocol.");
        }
    }

    // Transport/retry logic is shared; adapters differ only by the wire protocol.
    public abstract class OpenAiModelProvider : IModelProvider, IModelRequestSizer
    {
        private readonly HttpClient client;
        private readonly ModelOptions options;
        private readonly IOpenAiProtocol protocol;

        protected OpenAiModelProvider(HttpClient client, ModelOptions options, IOpenAiProtocol protocol)
        {
            this.client = client; this.options = options; this.protocol = protocol;
        }

        public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var body = protocol.WriteRequest(request).ToJsonString();
            var endpoint = new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), protocol.Path);
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrEmpty(options.ApiKey)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                    using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        bool transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                        if (transient && attempt < options.MaxRetries)
                        {
                            await Task.Delay(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        // Avoid exposing provider bodies or credentials in downstream responses/logs.
                        throw new ModelException("Upstream model HTTP status " + (int)response.StatusCode + ".");
                    }
                    using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                    try { return protocol.ReadResponse(json.RootElement); }
                    catch (Exception ex) when (ex is ProtocolException || ex is JsonException || ex is InvalidOperationException || ex is System.Collections.Generic.KeyNotFoundException)
                    {
                        throw new ModelException("Invalid upstream model response.", ex);
                    }
                }
                catch (HttpRequestException ex)
                {
                    if (attempt >= options.MaxRetries) throw new ModelException("Upstream model connection failed.", ex);
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // A timed-out model generation may have been billed; do not retry it blindly.
                    throw new ModelException("Upstream model request timed out.", ex);
                }
                catch (JsonException ex) { throw new ModelException("Invalid upstream JSON.", ex); }
            }
        }
        public int MeasureCharacters(ModelRequest request) => protocol.WriteRequest(request).ToJsonString().Length;

        private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
        {
            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt));
            return TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0, 30));
        }
    }

    public sealed class ResponsesModelProvider : OpenAiModelProvider
    {
        public ResponsesModelProvider(HttpClient client, ModelOptions options) : base(client, options, new ResponsesProtocol()) { }
    }

    public sealed class ChatCompletionsModelProvider : OpenAiModelProvider
    {
        public ChatCompletionsModelProvider(HttpClient client, ModelOptions options) : base(client, options, new ChatCompletionsProtocol()) { }
    }
}
