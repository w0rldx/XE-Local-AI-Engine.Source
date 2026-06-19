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
        return new HfDownloadClient(http, tokenStore, probe, options, NullLogger<HfDownloadClient>.Instance);
    }

    public static HuggingFaceGgufStore Store(HfDownloadClient downloadClient,
        IHuggingFaceGgufDiscovery discovery,
        GgufModelRegistry registry,
        HuggingFaceOptions options)
    {
        return new HuggingFaceGgufStore(downloadClient, discovery, registry, options, NullLogger<HuggingFaceGgufStore>.Instance);
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
        discovery.InspectRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(callInfo => Task.FromResult(new GgufRepoDetail(callInfo.ArgAt<string>(0),
                     false,
                     "apache-2.0",
                     files)));
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
