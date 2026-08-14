namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

public sealed class GgufDownloadTransactionTests
{
    private static readonly byte[] WeightBytes = "weight-content"u8.ToArray();
    private static readonly byte[] ProjectorBytes = "projector-content"u8.ToArray();

    [Test]
    public async Task PrepareAndCommit_ProjectorPair_PublishesAllArtifactsAndExactFingerprints()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, index) => Download(index == 0 ? WeightBytes : ProjectorBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var discovery = Discovery(includeProjector: true);
        var transaction = Transaction(http, discovery, registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);
        var destination = Destination(withProjector: true);

        var prepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        AssertEx.False(File.Exists(dir.FilePath(destination.RelativeGgufPath)));
        AssertEx.False(File.Exists(dir.FilePath(destination.ProjectorRelativePath!)));

        var receipt = await transaction.CommitAsync(prepared, CancellationToken.None);

        AssertEx.True(File.Exists(receipt.FinalGgufPath));
        AssertEx.True(File.Exists(receipt.FinalProjectorPath!));
        AssertEx.True(File.Exists(receipt.FinalSidecarPath));
        var installed = await registry.FindAsync(destination.CanonicalModelName, CancellationToken.None);
        AssertEx.NotNull(installed);
        AssertEx.Equal(Sha(WeightBytes), installed!.Sha256);
        AssertEx.Equal(Sha(ProjectorBytes), installed.ProjectorSha256);
        AssertEx.Equal(receipt.ModelContentFingerprint, installed.ModelContentFingerprint);
    }

    [Test]
    public async Task Prepare_ProjectorFailure_RemovesWeightAndProjectorTempsWithoutTextOnlySuccess()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, index) => index == 0
            ? Download(WeightBytes)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: true), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => transaction.PrepareAsync(source,
            Destination(withProjector: true),
            progress: null,
            CancellationToken.None));

        AssertEx.Equal(expected: 0, Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories).Count());
        AssertEx.Null(await registry.FindAsync(Destination(withProjector: true).CanonicalModelName, CancellationToken.None));
    }

    [Test]
    public async Task Commit_ProjectorDestinationAppearsAfterPrepare_RollsBackMovedSidecarAndNeverOverwrites()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, index) => Download(index == 0 ? WeightBytes : ProjectorBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: true), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);
        var destination = Destination(withProjector: true);
        var prepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        var collision = dir.FilePath(destination.ProjectorRelativePath!);
        await File.WriteAllTextAsync(collision, "preserve");

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => transaction.CommitAsync(prepared, CancellationToken.None));

        AssertEx.Equal("preserve", await File.ReadAllTextAsync(collision));
        AssertEx.False(File.Exists(dir.FilePath(destination.RelativeGgufPath)));
        AssertEx.False(File.Exists(dir.FilePath(destination.RelativeSidecarPath)));
        AssertEx.Null(await registry.FindAsync(destination.CanonicalModelName, CancellationToken.None));
        await transaction.DiscardPreparedAsync(prepared, CancellationToken.None);
    }

    [Test]
    public async Task Commit_PostRenameRegistryFailure_ReturnsOwnedReceiptForApplicationRollback()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) => Download(WeightBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: false), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);
        var prepared = await transaction.PrepareAsync(source, Destination(withProjector: false), progress: null, CancellationToken.None);
        var manifestPath = dir.FilePath("index.json");
        Directory.CreateDirectory(manifestPath);

        var exception = await AssertEx.ThrowsAsync<GgufDownloadCommitException>(() =>
            transaction.CommitAsync(prepared, CancellationToken.None));

        AssertEx.True(exception.CommitReceipt.OwnsFinalGguf);
        AssertEx.True(exception.CommitReceipt.OwnsFinalSidecar);
        AssertEx.False(exception.CommitReceipt.OwnsFinalProjector);
        Directory.Delete(manifestPath);
        await transaction.RollbackCommittedAsync(exception.CommitReceipt, CancellationToken.None);
        AssertEx.False(File.Exists(exception.CommitReceipt.FinalGgufPath));
        AssertEx.False(File.Exists(exception.CommitReceipt.FinalSidecarPath));
    }

    [Test]
    public async Task RollbackAndDiscard_ReportOwnedArtifactsThatCouldNotBeDeleted()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) => Download(WeightBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: false), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);
        var destination = Destination(withProjector: false);
        var committedPrepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        var receipt = await transaction.CommitAsync(committedPrepared, CancellationToken.None);
        File.Delete(receipt.FinalSidecarPath);
        Directory.CreateDirectory(receipt.FinalSidecarPath);

        _ = await AssertEx.ThrowsAsync<IOException>(() =>
            transaction.RollbackCommittedAsync(receipt, CancellationToken.None));
        AssertEx.True(Directory.Exists(receipt.FinalSidecarPath));
        Directory.Delete(receipt.FinalSidecarPath);
        File.Delete(receipt.FinalGgufPath);

        var discardPrepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        File.Delete(discardPrepared.TemporarySidecarPath);
        Directory.CreateDirectory(discardPrepared.TemporarySidecarPath);

        _ = await AssertEx.ThrowsAsync<IOException>(() =>
            transaction.DiscardPreparedAsync(discardPrepared, CancellationToken.None));
        AssertEx.True(Directory.Exists(discardPrepared.TemporarySidecarPath));
    }

    [Test]
    public async Task Resolve_ProjectorWithoutExactHash_FailsBeforeAnyArtifactIsReserved()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) => throw new InvalidOperationException("No bytes may be requested."));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var discovery = Discovery(includeProjector: true, projectorSha: null);
        var transaction = Transaction(http, discovery, registry, options);

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => transaction.ResolveAsync(
            new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant },
            CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(expected: 0, Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories).Count());
    }

    private static HuggingFaceGgufDownloadTransaction Transaction(HttpClient http,
        IHuggingFaceGgufDiscovery discovery,
        GgufModelRegistry registry,
        XE_Local_AI_Engine.Providers.HuggingFace.Options.HuggingFaceOptions options) =>
        new(Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options),
            discovery,
            registry,
            options,
            TimeProvider.System);

    private static IHuggingFaceGgufDiscovery Discovery(bool includeProjector, string? projectorSha = "computed")
    {
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, WeightBytes.Length, Sha(WeightBytes)));
        var resolvedProjectorSha = string.Equals(projectorSha, "computed", StringComparison.Ordinal)
            ? Sha(ProjectorBytes)
            : projectorSha;
        var projector = includeProjector
            ? new GgufProjectorFile("mmproj-model-f16.gguf",
                ProjectorBytes.Length,
                resolvedProjectorSha,
                Infra.Revision)
            : null;
        discovery.FindProjectorAsync(Infra.RepoId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(projector));
        return discovery;
    }

    private static GgufDownloadDestination Destination(bool withProjector)
    {
        var modelName = GgufModelName.Format(Infra.RepoId, Infra.Quant);
        return new GgufDownloadDestination(modelName,
            Infra.Quant,
            "demo-deterministic.gguf",
            "demo-deterministic.gguf.xe-model.json",
            withProjector ? "demo-projector.gguf" : null);
    }

    private static HttpResponseMessage Download(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
