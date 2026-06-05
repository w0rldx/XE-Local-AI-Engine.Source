namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fake;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Tests.ModelFit.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFitRefreshService" /> tests: the single non-bypass refresh path maps every runner
///     outcome to the right snapshot status (Succeeded / Failed / Cancelled / TimedOut), persists normalized rows on
///     success with the verified field mapping, stamps usage, tolerates malformed JSON without crashing, and re-throws
///     on a cancelled run after recording a Cancelled (not Failed) snapshot.
/// </summary>
public sealed class ModelFitRefreshServiceTests
{
    private const string ApprovedImageId = "llmfit-recommender-0-9-30";
    private const string ValidReference =
        "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

    // A representative two-model recommend payload with a system object (schema captured live).
    private const string TwoModelJson =
        """
        {
          "models": [
            {
              "name": "qwen2.5-coder:7b",
              "ollama_name": "qwen2.5-coder:7b",
              "score": 82.5,
              "fit_level": "Good",
              "run_mode": "GPU",
              "best_quant": "Q5_K_M",
              "estimated_tps": 48.2,
              "memory_required_gb": 6.0,
              "effective_context_length": 16384,
              "context_length": 32768,
              "installed": true,
              "category": "Coding",
              "is_moe": false,
              "params_b": 7.6,
              "score_components": { "quality": 80, "speed": 90, "fit": 75, "context": 85 }
            },
            {
              "name": "deepseek-coder:1.3b",
              "ollama_name": "deepseek-coder:1.3b",
              "score": 61.0,
              "fit_level": "Marginal",
              "run_mode": "CPU",
              "best_quant": "Q4_K_M",
              "estimated_tps": 12.0,
              "memory_required_gb": 1.5,
              "context_length": 16384,
              "installed": false
            }
          ],
          "system": { "total_ram_gb": 32, "cpu_cores": 16, "has_gpu": true, "gpu_vram_gb": 8 }
        }
        """;

    [Test]
    public async Task RefreshAsync_WhenRunSucceeds_PersistsRecommendationsAndStampsSuccessfulRun()
    {
        var harness = Harness.Create();
        harness.ScriptSucceeded(TwoModelJson);

        var result = await harness.Service.RefreshAsync(RecommendRequest(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(2, result.RecommendationCount);
        AssertEx.True(result.SnapshotId.HasValue, "a successful refresh has a snapshot id.");
        AssertEx.Null(result.SanitizedError);

        // Snapshot terminal status + raw/diagnostics stored, is_latest_successful set.
        var snapshot = harness.SnapshotStore.Snapshots[result.SnapshotId!.Value];
        AssertEx.Equal(ModelFitRunStatus.Succeeded, snapshot.Status);
        AssertEx.True(snapshot.IsLatestSuccessful, "a succeeded recommend snapshot must be marked latest-successful.");
        AssertEx.NotNull(snapshot.RawJson);
        AssertEx.NotNull(snapshot.DiagnosticsJson);
        AssertEx.Contains(snapshot.DiagnosticsJson!, "gpu_vram_gb");

        // Normalized rows: rank order + field mapping incl. GB→MB.
        var rows = harness.RecommendationStore.RowsFor(result.SnapshotId!.Value);
        AssertEx.Equal(2, rows.Count);
        AssertEx.Equal(1, rows[0].Rank);
        AssertEx.Equal("qwen2.5-coder:7b", rows[0].ModelName);
        AssertEx.Equal("qwen2.5-coder:7b", rows[0].ProviderModelName!);
        AssertEx.Equal(82.5, rows[0].Score);
        AssertEx.Equal("Good", rows[0].FitLevel!);
        AssertEx.Equal("GPU", rows[0].RunMode!);
        AssertEx.Equal("Q5_K_M", rows[0].Quantization!);
        AssertEx.Equal(48.2, rows[0].EstimatedTokensPerSecond ?? 0d);
        AssertEx.Equal(6.0 * 1024d, rows[0].RequiredRamMb ?? 0d); // GB → MB.
        AssertEx.Null(rows[0].RequiredVramMb);
        AssertEx.Equal(32768, rows[0].ContextTokens ?? 0); // Lane E: the model's real context_length, not the effective/estimation cap.
        AssertEx.True(rows[0].IsInstalled, "first model is installed.");
        AssertEx.Equal(2, rows[1].Rank);
        AssertEx.Equal(16384, rows[1].ContextTokens ?? 0); // context_length used directly (no effective on this model).
        AssertEx.False(rows[1].IsInstalled, "second model is not installed.");

        // Image usage stamped, with a successful-run stamp.
        var descriptor = harness.ApprovedImageStore.Records[ApprovedImageId];
        AssertEx.True(descriptor.LastUsedAtUtc.HasValue, "last-used must be stamped on a run.");
        AssertEx.True(descriptor.LastSuccessfulRunAtUtc.HasValue, "last-successful-run must be stamped on a success.");
    }

    [Test]
    public async Task RefreshAsync_WhenModelsArrayIsEmpty_SucceedsWithZeroRows()
    {
        var harness = Harness.Create();
        harness.ScriptSucceeded("""{ "models": [], "system": {} }""");

        var result = await harness.Service.RefreshAsync(RecommendRequest(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(0, result.RecommendationCount);
        AssertEx.True(harness.SnapshotStore.Snapshots[result.SnapshotId!.Value].IsLatestSuccessful, "empty-list success is still latest-successful.");
        AssertEx.Empty(harness.RecommendationStore.RowsFor(result.SnapshotId!.Value));
    }

    [Test]
    public async Task RefreshAsync_WhenJsonIsMalformed_RecordsFailedSnapshotWithoutRowsOrSuccessStamp()
    {
        var harness = Harness.Create();
        harness.ScriptSucceeded("this is not json at all");

        var result = await harness.Service.RefreshAsync(RecommendRequest(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Failed, result.Status);
        AssertEx.Equal(0, result.RecommendationCount);
        AssertEx.True(result.SnapshotId.HasValue, "a parse-failed run still recorded a snapshot.");
        var snapshot = harness.SnapshotStore.Snapshots[result.SnapshotId!.Value];
        AssertEx.Equal(ModelFitRunStatus.Failed, snapshot.Status);
        AssertEx.False(snapshot.IsLatestSuccessful, "a parse-failed run must not be latest-successful.");
        AssertEx.Empty(harness.RecommendationStore.RowsFor(result.SnapshotId!.Value));
        AssertEx.Null(harness.ApprovedImageStore.Records[ApprovedImageId].LastSuccessfulRunAtUtc);
    }

    [Test]
    public async Task RefreshAsync_WhenRunnerReturnsFailed_RecordsFailedSnapshotWithoutRowsOrSuccessStamp()
    {
        var harness = Harness.Create();
        harness.Runner.ScriptResult(new ModelFitUtilityRunResult(
            Status: ModelFitRunStatus.Failed,
            ExitCode: 1,
            StandardOutput: string.Empty,
            StandardError: "Error: provider unavailable",
            Completed: true,
            DurationMs: 10,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            SanitizedError: "model-fit utility run failed (exit code 1)"));

        var result = await harness.Service.RefreshAsync(RecommendRequest(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Failed, result.Status);
        AssertEx.Equal(0, result.RecommendationCount);
        var snapshot = harness.SnapshotStore.Snapshots[result.SnapshotId!.Value];
        AssertEx.Equal(ModelFitRunStatus.Failed, snapshot.Status);
        AssertEx.False(snapshot.IsLatestSuccessful);
        AssertEx.Empty(harness.RecommendationStore.RowsFor(result.SnapshotId!.Value));
        AssertEx.Null(harness.ApprovedImageStore.Records[ApprovedImageId].LastSuccessfulRunAtUtc);
    }

    [Test]
    public async Task RefreshAsync_WhenRunnerThrowsCancellation_RecordsCancelledSnapshotAndRethrows()
    {
        var harness = Harness.Create();
        harness.Runner.ScriptThrowCancellation();

        await AssertEx.ThrowsAsync<OperationCanceledException>(
            () => harness.Service.RefreshAsync(RecommendRequest(), reportProgress: null, CancellationToken.None));

        // Exactly one snapshot was opened and it is Cancelled — NOT Failed.
        AssertEx.Equal(1, harness.SnapshotStore.Snapshots.Count);
        var snapshot = harness.SnapshotStore.Snapshots.Values.Single();
        AssertEx.Equal(ModelFitRunStatus.Cancelled, snapshot.Status);
        AssertEx.False(snapshot.IsLatestSuccessful);
        AssertEx.Null(harness.ApprovedImageStore.Records[ApprovedImageId].LastSuccessfulRunAtUtc);
    }

    [Test]
    public async Task RefreshAsync_WhenBenchmarkOperation_FailsBeforeOpeningSnapshot()
    {
        var harness = Harness.Create();

        var result = await harness.Service.RefreshAsync(
            new ModelFitRefreshRequest(ApprovedImageId, ModelFitOperation.Benchmark, UseCase: null, Limit: 5, ProviderName: "ollama", ModelName: "llama3"),
            reportProgress: null,
            CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Failed, result.Status);
        AssertEx.Null(result.SnapshotId);
        AssertEx.Empty(harness.SnapshotStore.Snapshots.Values);
        AssertEx.Equal(0, harness.Runner.RunCount);
    }

    [Test]
    public async Task RefreshAsync_WhenImageRejected_FailsBeforeOpeningSnapshotOrRunning()
    {
        var harness = Harness.Create(enabled: false); // disabled descriptor → resolver rejection.

        var result = await harness.Service.RefreshAsync(RecommendRequest(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Failed, result.Status);
        AssertEx.Null(result.SnapshotId);
        AssertEx.Empty(harness.SnapshotStore.Snapshots.Values);
        AssertEx.Equal(0, harness.Runner.RunCount);
    }

    private static ModelFitRefreshRequest RecommendRequest() =>
        new(ApprovedImageId, ModelFitOperation.Recommend, UseCase: "coding", Limit: 5, ProviderName: "ollama", ModelName: null);

    private sealed class Harness
    {
        public required ModelFitRefreshService Service { get; init; }
        public required FakeModelFitUtilityRunner Runner { get; init; }
        public required InMemoryModelFitSnapshotStore SnapshotStore { get; init; }
        public required InMemoryModelFitRecommendationStore RecommendationStore { get; init; }
        public required InMemoryApprovedUtilityImageStore ApprovedImageStore { get; init; }

        public static Harness Create(bool enabled = true)
        {
            var runner = new FakeModelFitUtilityRunner();
            var snapshotStore = new InMemoryModelFitSnapshotStore();
            var recommendationStore = new InMemoryModelFitRecommendationStore();
            var approvedImageStore = new InMemoryApprovedUtilityImageStore(Descriptor(enabled));
            var resolver = new ApprovedImageResolver(approvedImageStore, new ApprovedImageReferenceValidator());
            var securityOptions = Options.Create(new SecurityOptions { AllowedModelNamePattern = "^[a-zA-Z0-9._:-]+$" });
            var validator = new ModelFitRequestValidator(new ModelNameValidator(securityOptions));

            var service = new ModelFitRefreshService(
                resolver,
                validator,
                new StubCapabilityReporter(),
                runner,
                snapshotStore,
                recommendationStore,
                approvedImageStore,
                TimeProvider.System,
                NullLogger<ModelFitRefreshService>.Instance);

            return new Harness
            {
                Service = service,
                Runner = runner,
                SnapshotStore = snapshotStore,
                RecommendationStore = recommendationStore,
                ApprovedImageStore = approvedImageStore
            };
        }

        public void ScriptSucceeded(string standardOutput) =>
            Runner.ScriptResult(new ModelFitUtilityRunResult(
                Status: ModelFitRunStatus.Succeeded,
                ExitCode: 0,
                StandardOutput: standardOutput,
                StandardError: string.Empty,
                Completed: true,
                DurationMs: 1234,
                StartedAtUtc: null,
                CompletedAtUtc: null,
                SanitizedError: null));

        private static ApprovedUtilityImageRecord Descriptor(bool enabled) =>
            new(
                ApprovedImageId: ApprovedImageId,
                DisplayName: "llmfit",
                Description: null,
                Purpose: UtilityImagePurpose.ModelRecommendation | UtilityImagePurpose.ModelBenchmark,
                ImageReference: ValidReference,
                SourceUrl: null,
                UpstreamVersion: "0.9.30",
                Enabled: enabled,
                DeprecatedAtUtc: null,
                ReplacementApprovedImageId: null,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                LastUsedAtUtc: null,
                LastSuccessfulRunAtUtc: null,
                DiagnosticsJson: null);
    }

    private sealed class StubCapabilityReporter : ICapabilityReporter
    {
        public Task<ClientCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClientCapabilities { RamMb = 32_768, VramMb = 8_192, CudaAvailable = true });

        public Task ReportToApiAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
