namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace;

/// <summary>
///     Shared, network-free test scaffolding for the Hugging Face GGUF store/registry/download tests: a scripted
///     <see cref="HttpMessageHandler" />, a fake <see cref="IFreeSpaceProbe" />, a temp models directory, and helpers to
///     wire a <see cref="HuggingFaceGgufStore" /> over a substituted <see cref="IHuggingFaceGgufDiscovery" />.
/// </summary>
internal static class GgufStoreTestInfrastructure
{
    public const string RepoId = "bartowski/Demo-Model-GGUF";
    public const string FileName = "Demo-Model-Q4_K_M.gguf";
    public const string Quant = "Q4_K_M";
    public const string Revision = "main";
    public static string ModelName => GgufModelName.Format(RepoId, Quant);

    public static HuggingFaceOptions Options(string modelsDir)
    {
        return new HuggingFaceOptions
        {
            ModelsDirectory = modelsDir,
            DiskMarginBytes = 0,
            DefaultQuant = Quant,
            MaxDownloadRetries = 2
        };
    }

    public static GgufModelRegistry Registry(HuggingFaceOptions options)
    {
        return new GgufModelRegistry(options, NullLogger<GgufModelRegistry>.Instance);
    }

    public static HfDownloadClient DownloadClient(HttpClient http,
        IHfTokenStore tokenStore,
        IFreeSpaceProbe probe,
        HuggingFaceOptions options)
    {
        // Default resolve probe: a bare 200 with no X-Linked-Etag → the client falls back to the byte response's own
        // X-Linked-Etag, preserving the pre-Xet test semantics. Xet-path tests pass an explicit resolve client below.
        // CA2000: this client wraps an in-memory fake handler (no sockets/unmanaged resource) and lives for the test's
        // duration as a field of the returned download client — GC-reclaimed at test end; disposing it here would break it.
#pragma warning disable CA2000
        var resolveHttp = new HttpClient(new ScriptedHandler(static (_, _) => new HttpResponseMessage()));
#pragma warning restore CA2000
        return DownloadClient(http, resolveHttp, tokenStore, probe, options);
    }

    public static HfDownloadClient DownloadClient(HttpClient http,
        HttpClient resolveHttp,
        IHfTokenStore tokenStore,
        IFreeSpaceProbe probe,
        HuggingFaceOptions options)
    {
        return new HfDownloadClient(http, resolveHttp, tokenStore, probe, options, NullLogger<HfDownloadClient>.Instance);
    }

    public static HuggingFaceGgufStore Store(HfDownloadClient downloadClient,
        IHuggingFaceGgufDiscovery discovery,
        GgufModelRegistry registry,
        HuggingFaceOptions options)
    {
        return new HuggingFaceGgufStore(downloadClient,
            discovery,
            registry,
            HeaderReader(options),
            options,
            NullLogger<HuggingFaceGgufStore>.Instance);
    }

    // The store only ever calls the reader's local-file path (ReadHeaderFromFileAsync), which never touches the HTTP
    // client — so a bare client over a throwing handler is safe and asserts no network read sneaks into the list path.
    public static GgufHeaderReader HeaderReader(HuggingFaceOptions options)
    {
#pragma warning disable CA2000
        var http = new HttpClient(new ScriptedHandler(static (_, _) =>
            throw new InvalidOperationException("The installed-model list must read headers from disk, never over HTTP.")));
#pragma warning restore CA2000
        return new GgufHeaderReader(http, options, NullLogger<GgufHeaderReader>.Instance);
    }

    public static IHfTokenStore NoTokenStore()
    {
        var store = Substitute.For<IHfTokenStore>();
        store.GetTokenAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        return store;
    }

    public static IHfTokenStore TokenStore(string token)
    {
        var store = Substitute.For<IHfTokenStore>();
        store.GetTokenAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(token));
        return store;
    }

    public static IFreeSpaceProbe AbundantSpace()
    {
        var probe = Substitute.For<IFreeSpaceProbe>();
        probe.GetAvailableFreeBytes(Arg.Any<string>()).Returns(long.MaxValue);
        return probe;
    }

    public static IFreeSpaceProbe FixedSpace(long availableBytes)
    {
        var probe = Substitute.For<IFreeSpaceProbe>();
        probe.GetAvailableFreeBytes(Arg.Any<string>()).Returns(availableBytes);
        return probe;
    }

    // Substitutes the discovery half so the store can resolve a quant → the canned file (the real discovery is
    // exercised by its own tests). Files default to one Q4_K_M file with the given size + sha; extra files can be appended.
    public static IHuggingFaceGgufDiscovery DiscoveryWith(params GgufRepoFile[] files)
    {
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // The store resolves via the header-free ListRepoFilesAsync; older/other callers use InspectRepoAsync. Stub
        // both with the same canned detail so either resolution path sees the seeded files.
        Task<GgufRepoDetail> Detail(NSubstitute.Core.CallInfo callInfo)
        {
            return Task.FromResult(new GgufRepoDetail(callInfo.ArgAt<string>(0), false, "apache-2.0", files));
        }

        discovery.ListRepoFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Detail);
        discovery.InspectRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Detail);
        return discovery;
    }

    public static GgufRepoFile RepoFile(string fileName, string quant, long sizeBytes, string? sha256 = null)
    {
        return new GgufRepoFile(fileName,
            quant,
            sizeBytes,
            sha256,
            Revision,
            "llama",
            quant,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    public static string Sha256Upper(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data));
    }

    /// <summary>A scripted HTTP handler returning queued responses (one per call), recording the requests it saw.</summary>
    public sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests => _requests;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = CallCount;
            CallCount++;
            _requests.Add(new RecordedRequest(request.Headers.Range?.ToString(),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            return Task.FromResult(responder(request, index));
        }
    }

    public sealed record RecordedRequest(string? Range, string? AuthScheme, string? AuthParameter);

    public sealed class TempModelsDir : IDisposable
    {
        public TempModelsDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-hf-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        public string FilePath(string fileName)
        {
            return System.IO.Path.Combine(Path, fileName);
        }
    }
}
