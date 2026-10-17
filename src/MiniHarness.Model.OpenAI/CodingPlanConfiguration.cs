using System;

namespace MiniHarness.Model.OpenAI
{
    public static class CodingPlanConfiguration
    {
        public static string? Read(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows()) value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows()) value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        // Treat credentials + endpoint + model as a bundle. Never send a real key
        // to an explicitly configured test/other-provider endpoint.
        public static bool ApplyWhenUnconfigured(ModelOptions options)
        {
            if (options.Model.Length > 0 || options.ApiKey.Length > 0 || options.BaseUrl.TrimEnd('/') != "https://api.openai.com/v1") return false;
            string? url = Read("CODING_PLAN_URL"), key = Read("CODING_PLAN_APIKEY"), model = Read("CODING_PLAN_MODEL");
            if (url == null || key == null || model == null) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
                endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
                throw new ArgumentException("CODING_PLAN_URL must be an HTTPS API base URL without credentials, query or fragment.");
            options.BaseUrl = url; options.ApiKey = key; options.Model = model; return true;
        }
    }
}
