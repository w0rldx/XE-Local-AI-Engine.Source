namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class GgufImportTransactionCoordinatorTests
{
    [Test]
    public async Task Preview_ReturnsSafeMetadataWithoutAbsoluteSourcePath()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath);

        var result = await coordinator.PreviewAsync(sourcePath);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);

        AssertEx.Equal("example", result.ModelBaseName);
        AssertEx.Equal("example:Q4_K_M", result.CanonicalModelName);
        AssertEx.Equal("example-Q4_K_M.gguf", result.SourceDisplayName);
        AssertEx.False(serialized.Contains(sourcePath, StringComparison.Ordinal));
        AssertEx.True(result.PreviewToken.Length >= 32);
        AssertEx.True(result.HasSufficientStorage is true);
    }

    [Test]
    public async Task Start_PreviewTokenUsedWithDifferentSource_IsRejectedBeforeReservation()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath);
        var preview = await coordinator.PreviewAsync(sourcePath);

        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(
            new StartGgufImportCommand(sourcePath + ".replacement",
                preview.PreviewToken,
                preview.ModelBaseName,
                "Q4_K_M")));

        AssertEx.Equal("InvalidPreviewToken", exception!.ErrorCode);
        AssertEx.False(exception.Message.Contains(sourcePath, StringComparison.Ordinal));
    }

    [Test]
    public async Task Preview_WhenQuantizationMustBeSelected_ReturnsRepositoryCanonicalChoices()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example.gguf");
        var inspection = AcceptedInspection(Path.GetFileName(sourcePath)) with
        {
            DetectedQuantization = null,
            Rejections = new[] { GgufImportRejectionCode.QuantizationRequired }
        };
        var coordinator = BuildCoordinator(sourcePath, inspection: inspection);

        var result = await coordinator.PreviewAsync(sourcePath);

        AssertEx.True(result.CanonicalQuantizationChoices.Count > 0);
        AssertEx.True(result.CanonicalQuantizationChoices.Contains("Q4_K_M", StringComparer.Ordinal));
        AssertEx.True(result.CanonicalQuantizationChoices.SequenceEqual(
            GgufAcquisitionIdentityResolver.CanonicalQuantizationChoices,
            StringComparer.Ordinal));
    }

    [Test]
    public async Task Preview_WhenFreeSpaceIsBelowFilePlusMargin_ReturnsFalse()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath, availableBytes: 50, diskMarginBytes: 9);

        var result = await coordinator.PreviewAsync(sourcePath);

        AssertEx.True(result.HasSufficientStorage is false);
    }

    [Test]
    public async Task Start_QuantizationNotOfferedByPreview_IsRejectedBeforeReservation()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath);
        var preview = await coordinator.PreviewAsync(sourcePath);

        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(
            new StartGgufImportCommand(sourcePath,
                preview.PreviewToken,
                preview.ModelBaseName,
                "Q5_K_M")));

        AssertEx.Equal("UnsupportedQuantization", exception!.ErrorCode);
    }

    private static GgufImportTransactionCoordinator BuildCoordinator(string sourcePath,
        GgufImportInspection? inspection = null,
        long availableBytes = long.MaxValue,
        long diskMarginBytes = 0)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var security = Options.Create(new SecurityOptions());
        return new GgufImportTransactionCoordinator(
            new AcceptedInspector(inspection ?? AcceptedInspection(Path.GetFileName(sourcePath))),
            new UnusedImporter(),
            new GgufAcquisitionIdentityResolver(new ModelNameValidator(security)),
            new GgufAcquisitionOperationRegistry(TimeProvider.System),
            services.GetRequiredService<IServiceScopeFactory>(),
            new NullGgufDownloadEventPublisher(),
            new FixedFreeSpaceProbe(availableBytes),
            new HuggingFaceOptions
            {
                ModelsDirectory = Path.GetTempPath(),
                DiskMarginBytes = diskMarginBytes
            },
            TimeProvider.System,
            NullLogger<GgufImportTransactionCoordinator>.Instance);
    }

    private static GgufImportInspection AcceptedInspection(string displayName) => new(42,
        GgufVersion: 3,
        Architecture: "llama",
        GgufImportWorkload.CausalChat,
        "Q4_K_M",
        displayName,
        Array.Empty<GgufImportRejectionCode>(),
        Array.Empty<string>());

    private sealed class AcceptedInspector(GgufImportInspection inspection) : IGgufImportInspector
    {
        public Task<GgufImportInspection> InspectAsync(GgufImportSource source, CancellationToken cancellationToken) =>
            Task.FromResult(inspection);
    }

    private sealed class FixedFreeSpaceProbe(long availableBytes) : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) => availableBytes;
    }

    private sealed class UnusedImporter : IGgufModelImporter
    {
        public Task<PreparedGgufImport> PrepareAsync(GgufImportSource source,
            GgufImportDestination destination,
            IProgress<GgufImportProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GgufImportCommitReceipt> CommitAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RollbackCommittedAsync(GgufImportCommitReceipt commitReceipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardPreparedAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
