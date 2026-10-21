using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness.Acceptance;
using MiniHarness.Harness.Delivery;

namespace MiniHarness.Harness
{
    public sealed partial class TaskRuntime
    {
        public async Task<object> ReadDeliveryAsync(string id, CancellationToken ct)
        {
            var task = await GetAsync(id, ct) ?? throw new ArgumentException("Task not found.");
            var state = task.Acceptance; bool? valid = null; string? failure = null;
            if (state?.Delivery?.Manifest != null)
            {
                try
                {
                    byte[] bytes = await delivery!.Artifacts.ReadAsync(id, state.Delivery.Manifest, ct);
                    var manifest = JsonSerializer.Deserialize<DeliveryManifest>(bytes, TaskJson.Options)!;
                    foreach (var artifact in manifest.Artifacts) await delivery.Artifacts.ReadAsync(id, artifact, ct);
                    valid = true;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is JsonException)
                { valid = false; failure = "delivery_integrity_failure"; }
            }
            return new { state?.Candidate, state?.Delivery, state?.Assessment, IntegrityValid = valid, IntegrityError = failure };
        }
        public async Task<AcceptanceAssessment> ReadAcceptanceAsync(string id, CancellationToken ct)
        {
            var task = await GetAsync(id, ct) ?? throw new ArgumentException("Task not found.");
            if (task.Acceptance == null) throw new ArgumentException("Legacy task has no V4 contract.");
            task.Facts.Workspace.Revision = await workspaceRevision.GetRevisionAsync(task.Facts.Workspace, ct);
            await CheckProtectionAsync(task, ct); var assessment = AcceptanceEvaluator.Evaluate(task);
            if (delivery!.Changes != null)
            { task.Acceptance.ChangeConflicts = (await delivery.Changes.FreezeAsync(task, ct)).Conflicts; assessment = AcceptanceEvaluator.Evaluate(task); }
            foreach (var artifact in task.Acceptance.FrozenArtifacts)
                try { await delivery!.Artifacts.ReadAsync(id, artifact, ct); }
                catch (IOException) { assessment.Blockers.Add("artifact_integrity:" + artifact.Id); assessment.CanFinalize = false; assessment.CanBuildCandidate = false; }
            return assessment;
        }
        public async Task<byte[]> ReadDeliveryArtifactAsync(string id, string artifactId, CancellationToken ct)
        {
            var task = await GetAsync(id, ct) ?? throw new ArgumentException("Task not found.");
            var reference = task.Acceptance?.FrozenArtifacts.SingleOrDefault(a => a.Id == artifactId);
            if (reference == null && task.Acceptance?.Delivery?.Manifest?.Id == artifactId) reference = task.Acceptance.Delivery.Manifest;
            if (reference == null && task.Acceptance?.Delivery?.Status == DeliveryStatus.Committed)
            {
                byte[] bytes = await delivery!.Artifacts.ReadAsync(id, task.Acceptance.Delivery.Manifest!, ct);
                reference = JsonSerializer.Deserialize<DeliveryManifest>(bytes, TaskJson.Options)!.Artifacts.SingleOrDefault(a => a.Id == artifactId);
            }
            if (reference == null) throw new ArgumentException("Artifact is not owned by this task.");
            return await delivery!.Artifacts.ReadAsync(id, reference, ct);
        }
        public async Task<byte[]> ReadDeliveryBundleAsync(string id, CancellationToken ct)
        {
            var task = await GetAsync(id, ct) ?? throw new ArgumentException("Task not found.");
            if (task.Acceptance?.Delivery?.Status != DeliveryStatus.Committed) throw new TaskConflictException("Delivery is not committed.");
            var manifestBytes = await delivery!.Artifacts.ReadAsync(id, task.Acceptance.Delivery.Manifest!, ct);
            var manifest = JsonSerializer.Deserialize<DeliveryManifest>(manifestBytes, TaskJson.Options)!;
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                async Task Add(string name, byte[] bytes)
                {
                    if (output.Length + bytes.LongLength > delivery.Options.MaxTaskBytes) throw new IOException("Bundle byte limit exceeded.");
                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var stream = entry.Open(); await stream.WriteAsync(bytes, ct);
                }
                await Add("manifest.json", manifestBytes);
                foreach (var artifact in manifest.Artifacts.GroupBy(a => a.Location).Select(g => g.First()).OrderBy(a => a.Location, StringComparer.Ordinal))
                    await Add(artifact.Location, await delivery.Artifacts.ReadAsync(id, artifact, ct));
            }
            return output.ToArray();
        }
    }
}
