using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding.Processes
{
    internal sealed class ProcessOutputStore
    {
        private readonly object sync = new object();
        private readonly string path;
        private readonly ProcessSessionOptions options;
        private readonly List<ProcessOutputChunk> chunks = new List<ProcessOutputChunk>();
        private long sequence;
        public bool Degraded { get; private set; }
        public ProcessOutputStore(string path, ProcessSessionOptions options)
        {
            this.path = path; this.options = options;
            if (File.Exists(path))
            {
                var loaded = ProcessJson.Read<ProcessOutputPage>(path);
                chunks.AddRange(loaded.Chunks); sequence = loaded.NextCursor; Trim();
            }
        }
        public long Last { get { lock (sync) return sequence; } }
        public ArtifactReference? Seal(string resourceId, string taskId)
        {
            lock (sync)
            {
                if (!File.Exists(path) || Degraded) return null;
                return new ArtifactReference { Id = "output_" + resourceId, OwnerTaskId = taskId,
                    Location = path, ContentHash = WorkspaceAccess.HashBytes(File.ReadAllBytes(path)) };
            }
        }
        public long First { get { lock (sync) return chunks.Count == 0 ? sequence + 1 : chunks[0].Sequence; } }
        public void Append(string stream, string text)
        {
            lock (sync)
            {
                // Rune iteration keeps UTF-8 sequences intact, including emoji.
                var buffer = new StringBuilder(); int bytes = 0;
                void Add()
                {
                    if (buffer.Length == 0) return;
                    chunks.Add(new ProcessOutputChunk { Sequence = ++sequence, Stream = stream, Text = buffer.ToString() }); buffer.Clear(); bytes = 0;
                    while (chunks.Count > 256) chunks.RemoveAt(0);
                }
                foreach (var rune in text.EnumerateRunes())
                {
                    if (bytes + rune.Utf8SequenceLength > 4) Add();
                    buffer.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
                }
                Add(); Trim();
                try { ProcessJson.Write(path, Snapshot()); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Degraded = true; }
            }
        }
        private ProcessOutputPage Snapshot() => new ProcessOutputPage { Chunks = chunks.ToList(), NextCursor = sequence,
            FirstAvailableSequence = First, Gap = First > 1, Truncated = Degraded || First > 1 };
        private void Trim()
        {
            while (chunks.Count > 256 || chunks.Sum(c => Encoding.UTF8.GetByteCount(c.Text)) > options.MaxOutputMemoryBytes ||
                Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Snapshot(), ProcessJson.Options)) > options.MaxOutputDiskBytes)
            { if (chunks.Count == 0) break; chunks.RemoveAt(0); }
        }
        public ProcessOutputPage Read(long after, int limit)
        {
            lock (sync)
            {
                if (after < 0 || after > sequence) throw new ArgumentException("Invalid process output cursor.");
                if (limit < 256) throw new ArgumentException("Output pages require a minimum 256-byte JSON budget.");
                limit = Math.Min(limit, options.MaxPageBytes);
                var page = new ProcessOutputPage { FirstAvailableSequence = First, Gap = after < First - 1,
                    NextCursor = Math.Max(after, First - 1), Truncated = Degraded || First > 1 };
                foreach (var chunk in chunks.Where(c => c.Sequence > after))
                {
                    long previous = page.NextCursor;
                    page.Chunks.Add(ProcessJson.Clone(chunk)); page.NextCursor = chunk.Sequence;
                    page.HasMore = page.NextCursor < sequence;
                    if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(page, ProcessJson.Options)) > limit)
                    { page.Chunks.RemoveAt(page.Chunks.Count - 1); page.NextCursor = previous; break; }
                }
                page.HasMore = page.NextCursor < sequence;
                return page;
            }
        }
    }
}
