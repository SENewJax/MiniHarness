using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace MiniHarness.Coding.Processes
{
    internal static class ProcessJson
    {
        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
        internal static T Read<T>(string path)
        {
            // A reader keeps one immutable file version alive while atomic
            // replacement publishes the next. Do not hold a striped Monitor
            // around I/O: output/session locks may otherwise wait behind it.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(file, Options)!;
        }
        internal static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options)); file.Write(bytes); file.Flush(true); }
                for (int attempt = 0; ; attempt++)
                    try { File.Move(temporary, path, true); break; }
                    catch (Exception ex) when (attempt < 6 && (ex is IOException || ex is UnauthorizedAccessException) &&
                        ((uint)ex.HResult & 0xffff0000u) == 0x80070000u && (ex.HResult & 0xffff) is 5 or 32 or 33)
                    { Thread.Sleep(10 << attempt); }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
