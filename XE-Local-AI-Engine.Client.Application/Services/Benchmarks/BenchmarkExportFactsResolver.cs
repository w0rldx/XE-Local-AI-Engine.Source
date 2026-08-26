namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed record BenchmarkFidelityDisplayFacts(string? ExpectedKldDigest)
{
    public static BenchmarkFidelityDisplayFacts FromProject(BenchmarkProjectRecord? project) =>
        new(project is { FidelityKldEnabled: true, FidelityKldBaseFingerprint: { Length: > 0 } fingerprint }
            ? BenchmarkKldCacheKey.Create(fingerprint,
                    BenchmarkFidelityCorpus.Require().Sha256,
                    BenchmarkFidelityPolicy.ClampChunks(project.FidelityChunks))
                .Digest
            : null);
}

public interface IBenchmarkExportFactsResolver
{
    BenchmarkFidelityDisplayFacts ResolveProject(BenchmarkProjectRecord project);

    BenchmarkExportRunFacts ResolveRun(BenchmarkRunRecord run);
}

internal sealed class BenchmarkExportFactsResolver(
    IBenchmarkRuntimeSnapshotFactory snapshots,
    ILogger<BenchmarkExportFactsResolver> logger) : IBenchmarkExportFactsResolver
{
    private readonly ILogger<BenchmarkExportFactsResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IBenchmarkRuntimeSnapshotFactory _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));

    public BenchmarkFidelityDisplayFacts ResolveProject(BenchmarkProjectRecord project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return BenchmarkFidelityDisplayFacts.FromProject(project);
    }

    public BenchmarkExportRunFacts ResolveRun(BenchmarkRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);

        string? modelFilename = null;
        long? modelSize = null;
        int? gpuLayers = null;
        try
        {
            var snapshot = _snapshots.Deserialize(run.RuntimeSnapshotJson.Span);
            var weights = snapshot.PrimaryModel.Members
                .Where(static member => member.Role == InstalledModelPhysicalMemberRole.Weight)
                .ToArray();
            modelFilename = snapshot.PrimaryModel.SourceFileName ?? weights.FirstOrDefault()?.RelativePath;
            modelSize = weights.Length == 0 ? null : weights.Sum(static member => member.SizeBytes);
            gpuLayers = snapshot.PrimaryRuntime.GpuLayers;
        }
        catch (Exception exception) when (exception is BenchmarkSnapshotException or JsonException)
        {
            _logger.LogWarning(exception, "Benchmark export: run {RunId} carries a snapshot that could not be read.", run.Id);
        }

        string? buildCommit = null;
        string? gpuInfo = null;
        if (run.PrimaryLaunchEvidence?.EnvironmentFactsJson is { } environmentJson && !environmentJson.IsEmpty)
        {
            try
            {
                var environment = BenchmarkCanonicalJson.Deserialize<RuntimeEnvironmentFactsV1>(environmentJson.Span);
                buildCommit = environment?.LlamaRuntime?.SourceCommit ?? environment?.LlamaRuntime?.Version;
                gpuInfo = environment?.Hardware?.Gpus is { Count: > 0 } gpus
                    ? string.Join(", ", gpus.Select(static gpu => gpu.Name))
                    : null;
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Benchmark export: run {RunId} carries environment facts that could not be read.", run.Id);
            }
        }

        return new BenchmarkExportRunFacts(buildCommit, gpuInfo, modelFilename, modelSize, gpuLayers);
    }
}
