namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     The Whisper weight store's contract is <b>ensure</b>, not download: a file already on disk that matches the
///     pinned catalogue is reused without touching the network.
/// </summary>
/// <remarks>
///     This is the regression suite for the defect the S1a live round found. Every transcription model download
///     fetches the SHARED voice-activity-detection file as its first part, so on a node that already installed one
///     model that file exists when the next download starts. The store called the download client unconditionally;
///     the client re-fetched the bytes, verified them, and then refused to publish them onto the existing destination
///     — failing the whole download before the weights the operator asked for were ever requested. The second model
///     an operator installed could therefore never succeed.
///
///     The real store over the real <see cref="HfDownloadClient" /> is used throughout, because those two together
///     are what failed; only the transport is faked.
/// </remarks>
public sealed class HuggingFaceWhisperWeightStoreTests
{
    private static readonly byte[] SharedBytes = Encoding.UTF8.GetBytes(new string(c: 'v', count: 2048));
    private static readonly byte[] WeightBytes = Encoding.UTF8.GetBytes(new string(c: 'w', count: 4096));

    [Test]
    public async Task EnsureFile_DestinationAlreadyMatchesTheCatalogue_ReportsCompletedWithoutTouchingTheNetwork()
    {
        using var dir = new Infra.TempModelsDir();
        var destination = dir.FilePath("ggml-silero.bin");
        await File.WriteAllBytesAsync(destination, SharedBytes);
        using var harness = new Harness(dir);
        var progress = new RecordingProgress();

        var path = await harness.Store.EnsureFileAsync(Request(destination, SharedBytes), progress, CancellationToken.None);

        AssertEx.Equal(expected: 0,
            harness.Handler.CallCount,
            "A file already matching the pinned size and digest must be reused, not fetched again.");
        AssertEx.Equal(destination, path);
        var reported = AssertEx.NotNull(progress.Last);
        AssertEx.Equal("completed", reported.Status);
        AssertEx.Equal((long?)SharedBytes.Length, reported.CompletedBytes);
        AssertEx.Equal((long?)SharedBytes.Length, reported.TotalBytes);
    }

    [Test]
    public async Task EnsureFile_DestinationAlreadyMatches_RemovesTheResidueOfAnEarlierFailedCommit()
    {
        // Exactly what the live round left on disk: a complete .part and its range sidecar beside a published file,
        // orphaned by an attempt that died at the commit step. The destination is already published, so those bytes
        // are dead weight rather than resume state, and leaving them means every later run carries the wreckage.
        using var dir = new Infra.TempModelsDir();
        var destination = dir.FilePath("ggml-silero.bin");
        await File.WriteAllBytesAsync(destination, SharedBytes);
        var partPath = destination + ".part";
        await File.WriteAllBytesAsync(partPath, SharedBytes);
        await File.WriteAllTextAsync(partPath + ".ranges.part", "stale");
        using var harness = new Harness(dir);

        _ = await harness.Store.EnsureFileAsync(Request(destination, SharedBytes), progress: null, CancellationToken.None);

        AssertEx.False(File.Exists(partPath), "The orphaned .part beside a published file must be removed.");
        AssertEx.False(File.Exists(partPath + ".ranges.part"), "The orphaned range sidecar must be removed with its .part.");
        AssertEx.True(File.Exists(destination), "The published file itself must survive the cleanup.");
    }

    [Test]
    public async Task EnsureFile_DestinationExistsWithForeignBytes_IsReplacedByAFreshDownload()
    {
        // Present but not what the catalogue pins — a truncated copy, an upstream re-upload, or a foreign file under
        // our name. Reusing it would install bytes the runtime cannot load; leaving it would fail the commit guard
        // forever. It is replaced.
        using var dir = new Infra.TempModelsDir();
        var destination = dir.FilePath("ggml-silero.bin");
        await File.WriteAllBytesAsync(destination, Encoding.UTF8.GetBytes("not the pinned weights"));
        using var harness = new Harness(dir);

        var path = await harness.Store.EnsureFileAsync(Request(destination, SharedBytes), progress: null, CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Handler.CallCount, "A destination that fails verification must be re-downloaded.");
        var finalBytes = await File.ReadAllBytesAsync(path);
        AssertEx.True(finalBytes.SequenceEqual(SharedBytes), "The replaced file must be the bytes the catalogue pins.");
    }

    [Test]
    public async Task EnsureFile_DestinationHasTheRightLengthButWrongBytes_IsStillReplaced()
    {
        // The size check is only the cheap gate; the digest is what decides. A corrupt file of exactly the right
        // length must not pass as installed.
        using var dir = new Infra.TempModelsDir();
        var destination = dir.FilePath("ggml-silero.bin");
        var sameLengthDifferentBytes = Encoding.UTF8.GetBytes(new string(c: 'x', count: SharedBytes.Length));
        await File.WriteAllBytesAsync(destination, sameLengthDifferentBytes);
        using var harness = new Harness(dir);

        _ = await harness.Store.EnsureFileAsync(Request(destination, SharedBytes), progress: null, CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Handler.CallCount, "A same-length file with a different digest must be re-downloaded.");
        AssertEx.True((await File.ReadAllBytesAsync(destination)).SequenceEqual(SharedBytes));
    }

    [Test]
    public async Task TwoModelInstallsSharingOneFile_TheSecondReusesTheSharedFileAndStillFetchesItsOwnWeight()
    {
        // The live defect in miniature, driven through the real store and the real download client: install one model
        // (shared file + its weight), then install a second (the same shared file + a different weight). Before the
        // fix the second install died on the shared file's commit guard and never requested its weight at all.
        using var dir = new Infra.TempModelsDir();
        using var harness = new Harness(dir);
        var shared = dir.FilePath("ggml-silero.bin");
        var firstWeight = dir.FilePath("ggml-base.bin");
        var secondWeight = dir.FilePath("ggml-large.bin");

        _ = await harness.Store.EnsureFileAsync(Request(shared, SharedBytes), progress: null, CancellationToken.None);
        _ = await harness.Store.EnsureFileAsync(Request(firstWeight, WeightBytes), progress: null, CancellationToken.None);
        var callsAfterFirstInstall = harness.Handler.CallCount;

        _ = await harness.Store.EnsureFileAsync(Request(shared, SharedBytes), progress: null, CancellationToken.None);
        var path = await harness.Store.EnsureFileAsync(Request(secondWeight, WeightBytes), progress: null, CancellationToken.None);

        AssertEx.Equal(expected: 2, callsAfterFirstInstall, "The first install fetches both of its files.");
        AssertEx.Equal(expected: 3,
            harness.Handler.CallCount,
            "The second install must skip the shared file and fetch only its own weight.");
        AssertEx.Equal(secondWeight, path);
        AssertEx.True(File.Exists(secondWeight), "The second model's weight is the file the operator actually asked for.");
    }

    private static WhisperWeightFileRequest Request(string destinationPath, byte[] bytes)
    {
        return new WhisperWeightFileRequest
        {
            RepoId = "ggml-org/whisper-test",
            FileName = Path.GetFileName(destinationPath),
            DestinationPath = destinationPath,
            ExpectedSizeBytes = bytes.Length,
            ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ProgressLabel = "test-model"
        };
    }

    /// <summary>Serves whichever of the two canned payloads the request URI names, and counts every call.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;
        private readonly HttpClient _resolveHttp;
        private readonly Infra.ScriptedHandler _resolveHandler;

        public Harness(Infra.TempModelsDir dir)
        {
            Handler = new Infra.ScriptedHandler((request, _) =>
            {
                var bytes = request.RequestUri!.AbsoluteUri.Contains("silero", StringComparison.OrdinalIgnoreCase)
                    ? SharedBytes
                    : WeightBytes;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
                response.Content.Headers.ContentLength = bytes.Length;
                response.Headers.TryAddWithoutValidation("X-Repo-Commit", "abc123def456");
                return response;
            });
            _http = new HttpClient(Handler, disposeHandler: false);

            // A bare 200 with no X-Linked-Etag, so the client verifies against the caller's pinned digest — which is
            // the path the transcription catalogue actually takes.
            _resolveHandler = new Infra.ScriptedHandler(static (_, _) => new HttpResponseMessage());
            _resolveHttp = new HttpClient(_resolveHandler, disposeHandler: false);

            var options = Infra.Options(dir.Path);
            Store = new HuggingFaceWhisperWeightStore(Infra.DownloadClient(_http,
                _resolveHttp,
                Infra.NoTokenStore(),
                Infra.AbundantSpace(),
                options));
        }

        public Infra.ScriptedHandler Handler { get; }

        public HuggingFaceWhisperWeightStore Store { get; }

        public void Dispose()
        {
            _http.Dispose();
            _resolveHttp.Dispose();
            Handler.Dispose();
            _resolveHandler.Dispose();
        }
    }

    private sealed class RecordingProgress : IProgress<PullProgress>
    {
        public PullProgress? Last { get; private set; }

        public void Report(PullProgress value) => Last = value;
    }
}
