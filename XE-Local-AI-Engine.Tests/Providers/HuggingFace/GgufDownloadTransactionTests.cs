namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

[Category(TestCategories.Unit)]
public sealed class GgufDownloadTransactionTests
{
    private static readonly byte[] WeightBytes = "weight-content"u8.ToArray();
    private static readonly byte[] ProjectorBytes = "projector-content"u8.ToArray();

    [Test]
    public async Task PrepareAndCommit_ProjectorPair_PublishesAllArtifactsAndExactFingerprints()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, index) => Download(index == 0 ? WeightBytes : ProjectorBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var discovery = Discovery(includeProjector: true);
        var transaction = Transaction(http, discovery, registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = Infra.Quant
        }, CancellationToken.None);
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
    public async Task ResolveAndCommit_WeightsOnlyRequest_NeverScansForAProjectorAndCommitsNoProjectorFacts()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => Download(WeightBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        // The repo DOES ship a projector: only the request says not to take it.
        var discovery = Discovery(includeProjector: true);
        var transaction = Transaction(http, discovery, registry, options);

        var source = await transaction.ResolveAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = Infra.Quant,
            IncludeProjector = false
        }, CancellationToken.None);

        AssertEx.Null(source.Projector);
        await discovery.DidNotReceive().FindProjectorAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        var destination = Destination(withProjector: false);
        var prepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        AssertEx.Null(prepared.TemporaryProjectorPath);
        AssertEx.Null(prepared.Sidecar.ProjectorRelativePath);
        AssertEx.Null(prepared.Sidecar.ProjectorContentSha256);
        AssertEx.Null(prepared.Sidecar.ProjectorSourceDisplayName);
        AssertEx.Null(prepared.Sidecar.ProjectorMemberFingerprint);

        var receipt = await transaction.CommitAsync(prepared, CancellationToken.None);

        AssertEx.Null(receipt.FinalProjectorPath);
        var installed = await registry.FindAsync(destination.CanonicalModelName, CancellationToken.None);
        AssertEx.NotNull(installed);
        AssertEx.Null(installed!.ProjectorFileName);
        AssertEx.Null(installed.ProjectorLocalPath);
        AssertEx.Null(installed.ProjectorSha256);
        AssertEx.Null(installed.ProjectorSizeBytes);
        // Exactly the weight and its sidecar reached the models directory — no projector bytes were fetched.
        AssertEx.Equal(expected: 1, handler.CallCount);
    }

    [Test]
    public async Task Prepare_ProjectorFailsAfterTheWeightVerified_KeepsTheWeightResumableAndTheRetryDoesNotRefetchIt()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var projectorFails = true;
        var weightRequests = 0;
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("mmproj", StringComparison.Ordinal))
            {
                return projectorFails ? new HttpResponseMessage(HttpStatusCode.NotFound) : Download(ProjectorBytes);
            }

            weightRequests++;
            return Download(WeightBytes);
        });
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: true), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = Infra.Quant
        }, CancellationToken.None);
        var destination = Destination(withProjector: true);
        var weightPart = dir.FilePath(destination.RelativeGgufPath) + ".part";

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => transaction.PrepareAsync(source,
            destination,
            progress: null,
            CancellationToken.None));

        // No text-only success: nothing final, nothing registered — but the verified weight waits at its resumable name.
        AssertEx.Equal(Path.GetFileName(weightPart) + " | " + Path.GetFileName(weightPart) + ".ranges.part",
            string.Join(" | ", Directory.EnumerateFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
        AssertEx.Equal(Sha(WeightBytes), Sha(await File.ReadAllBytesAsync(weightPart)));
        AssertEx.Null(await registry.FindAsync(destination.CanonicalModelName, CancellationToken.None));

        projectorFails = false;
        var prepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        var receipt = await transaction.CommitAsync(prepared, CancellationToken.None);

        AssertEx.Equal(expected: 1, weightRequests);
        AssertEx.Equal(Sha(WeightBytes), Sha(await File.ReadAllBytesAsync(receipt.FinalGgufPath)));
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
    }

    [Test]
    public async Task Commit_ProjectorDestinationAppearsAfterPrepare_ReturnsPartialReceiptForSafeRollback()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, index) => Download(index == 0 ? WeightBytes : ProjectorBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: true), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = Infra.Quant
        }, CancellationToken.None);
        var destination = Destination(withProjector: true);
        var prepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        var collision = dir.FilePath(destination.ProjectorRelativePath!);
        await File.WriteAllTextAsync(collision, "preserve");

        var exception = await AssertEx.ThrowsAsync<GgufDownloadCommitException>(() => transaction.CommitAsync(prepared, CancellationToken.None));
        AssertEx.False(exception.CommitReceipt.OwnsFinalGguf);
        AssertEx.True(exception.CommitReceipt.OwnsFinalSidecar);
        AssertEx.False(exception.CommitReceipt.OwnsFinalProjector);
        await transaction.RollbackCommittedAsync(exception.CommitReceipt, CancellationToken.None);

        AssertEx.Equal("preserve", await File.ReadAllTextAsync(collision));
        AssertEx.False(File.Exists(dir.FilePath(destination.RelativeGgufPath)));
        AssertEx.False(File.Exists(dir.FilePath(destination.RelativeSidecarPath)));
        AssertEx.Null(await registry.FindAsync(destination.CanonicalModelName, CancellationToken.None));
        await transaction.DiscardPreparedAsync(prepared, CancellationToken.None);
    }

    [Test]
    public async Task Commit_PostRenameRegistryFailure_ReturnsOwnedReceiptForApplicationRollback()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => Download(WeightBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: false), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = Infra.Quant
        }, CancellationToken.None);
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
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => Download(WeightBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: false), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = Infra.Quant
        }, CancellationToken.None);
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
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => throw new InvalidOperationException("No bytes may be requested."));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var discovery = Discovery(includeProjector: true, projectorSha: null);
        var transaction = Transaction(http, discovery, registry, options);

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => transaction.ResolveAsync(new GgufModelRequest
            {
                RepoId = Infra.RepoId,
                Quant = Infra.Quant
            },
            CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(expected: 0, Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories).Count());
    }

    [Test]
    public async Task Prepare_InterruptedDownload_KeepsOneSingleSuffixPartialThatTheNextPrepareResumes()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        options.MaxDownloadRetries = 0;
        const int prefix = 6;
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, index) => index == 0
            ? Interrupted(WeightBytes, prefix, static () => throw new IOException("No space left on device.", hresult: 28))
            : Resumed(WeightBytes, prefix));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: false), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);
        var destination = Destination(withProjector: false);
        var partPath = dir.FilePath(destination.RelativeGgufPath) + ".part";

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() =>
            transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None));

        // Exactly one partial under the stable single-suffix name, plus its resume cursors beside it.
        AssertEx.Equal(Path.GetFileName(partPath) + " | " + Path.GetFileName(partPath) + ".ranges.part",
            string.Join(" | ", Directory.EnumerateFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
        AssertEx.Equal(prefix, (int)new FileInfo(partPath).Length);

        var prepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        var receipt = await transaction.CommitAsync(prepared, CancellationToken.None);

        AssertEx.Contains(handler.Requests[1].Range, prefix.ToString(CultureInfo.InvariantCulture) + "-");
        AssertEx.Equal(Sha(WeightBytes), Sha(await File.ReadAllBytesAsync(receipt.FinalGgufPath)));
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
    }

    [Test]
    public async Task Prepare_Cancelled_DeletesThePartialAndItsResumeCursors()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var cts = new CancellationTokenSource();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => Interrupted(WeightBytes, cutAt: 6, () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: false), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            transaction.PrepareAsync(source, Destination(withProjector: false), progress: null, cts.Token));

        AssertEx.Empty(Directory.EnumerateFiles(dir.Path));
    }

    [Test]
    public async Task Prepare_CancellationTheOperatorDidNotRequest_KeepsThePartialAndItsResumeCursors()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // An idle timeout or a torn-down transport surfaces as a cancellation the caller's token never asked for.
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
            Interrupted(WeightBytes, cutAt: 6, static () => throw new OperationCanceledException()));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: false), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);
        var destination = Destination(withProjector: false);
        var partPath = dir.FilePath(destination.RelativeGgufPath) + ".part";

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None));

        AssertEx.Equal(expected: 6, (int)new FileInfo(partPath).Length);
        AssertEx.True(File.Exists(partPath + ".ranges.part"), "the resume cursors must survive a cancellation nobody requested");
    }

    [Test]
    public async Task PrepareAndCommit_LegacyDoubleSuffixedPartials_NewestResumesAndNoPartialOutlivesTheCompletedDownload()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        const int prefix = 6;
        var destination = Destination(withProjector: false);
        var finalPath = dir.FilePath(destination.RelativeGgufPath);
        // Two attempts from the per-operation layout: an older abandoned one and the newest, which holds a vouched-for prefix.
        var olderPart = finalPath + ".11111111111111111111111111111111.part.part";
        var newestPart = finalPath + ".22222222222222222222222222222222.part.part";
        await File.WriteAllBytesAsync(olderPart, WeightBytes[..3]);
        await File.WriteAllTextAsync(olderPart + ".ranges.part", "stale");
        File.SetLastWriteTimeUtc(olderPart, DateTime.UtcNow - TimeSpan.FromHours(1));
        await File.WriteAllBytesAsync(newestPart, WeightBytes[..prefix]);
        await File.WriteAllTextAsync(newestPart + ".ranges.part",
            string.Create(CultureInfo.InvariantCulture, $"2 {WeightBytes.Length} {Infra.Revision} {prefix}"));
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => Resumed(WeightBytes, prefix));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var transaction = Transaction(http, Discovery(includeProjector: false), registry, options);
        var source = await transaction.ResolveAsync(new GgufModelRequest { RepoId = Infra.RepoId, Quant = Infra.Quant }, CancellationToken.None);

        var prepared = await transaction.PrepareAsync(source, destination, progress: null, CancellationToken.None);
        var receipt = await transaction.CommitAsync(prepared, CancellationToken.None);

        AssertEx.Contains(handler.Requests[0].Range, prefix.ToString(CultureInfo.InvariantCulture) + "-");
        AssertEx.Equal(Sha(WeightBytes), Sha(await File.ReadAllBytesAsync(receipt.FinalGgufPath)));
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
    }

    private static HuggingFaceGgufDownloadTransaction Transaction(HttpClient http,
        IHuggingFaceGgufDiscovery discovery,
        GgufModelRegistry registry,
        HuggingFaceOptions options) =>
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
            ? new GgufProjectorFile
            {
                FileName = "mmproj-model-f16.gguf",
                SizeBytes = ProjectorBytes.Length,
                Sha256 = resolvedProjectorSha,
                Revision = Infra.Revision
            }
            : null;
        discovery.FindProjectorAsync(Infra.RepoId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(projector));
        return discovery;
    }

    private static GgufDownloadDestination Destination(bool withProjector)
    {
        var modelName = GgufModelName.Format(Infra.RepoId, Infra.Quant);
        return new GgufDownloadDestination
        {
            CanonicalModelName = modelName,
            CanonicalQuant = Infra.Quant,
            RelativeGgufPath = "demo-deterministic.gguf",
            RelativeSidecarPath = "demo-deterministic.gguf.xe-model.json",
            ProjectorRelativePath = withProjector ? "demo-projector.gguf" : null
        };
    }

    // A 200 whose body yields the first cutAt bytes, then runs interrupt (which throws) — an attempt stopped mid-copy.
    private static HttpResponseMessage Interrupted(byte[] bytes, int cutAt, Action interrupt)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptingStream(bytes, cutAt, interrupt)) };
        response.Content.Headers.ContentLength = bytes.Length;
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", Infra.Revision);
        return response;
    }

    // A 206 serving [from, end) at the same commit the interrupted attempt recorded.
    private static HttpResponseMessage Resumed(byte[] bytes, int from)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[from..]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", Infra.Revision);
        return response;
    }

    private static HttpResponseMessage Download(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };

    private static string Sha(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class InterruptingStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _cutAt;
        private readonly Action _interrupt;
        private int _position;

        public InterruptingStream(byte[] bytes, int cutAt, Action interrupt)
        {
            _bytes = bytes;
            _cutAt = cutAt;
            _interrupt = interrupt;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _cutAt)
            {
                _interrupt();
            }

            var toCopy = Math.Min(buffer.Length, _cutAt - _position);
            _bytes.AsSpan(_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
