using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed class FileRunStore : IRunStore
    {
        private readonly string directory;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        public FileRunStore(string directory)
        {
            this.directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(this.directory);
        }
        public async Task SaveAsync(RunState state, CancellationToken cancellationToken)
        {
            string path = GetPath(state.Id);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // WriteThrough + Flush(true) before atomic same-directory replacement.
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        public async Task<RunState?> LoadAsync(string id, CancellationToken cancellationToken)
        {
            string path = GetPath(id);
            try
            {
                using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<RunState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return null; }
        }
        private string GetPath(string id)
        {
            if (!Regex.IsMatch(id, "^resp_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
                throw new ArgumentException("Invalid response ID.");
            return Path.Combine(directory, id + ".json");
        }
    }
}
