namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Live GitHub Releases catalog: asset/digest parsing, ETag-conditional reuse, graceful offline + rate-limit
///     handling, asset-name templating per OS/variant, and tag-format rejection. All HTTP is faked — no network.
/// </summary>
public sealed class GitHubLlamaCppReleaseCatalogTests
{
    private const string Tag = "b9692";

    [Test]
    public async Task ResolveAsset_WhenLiveApiReturnsRelease_ParsesAssetNameUrlDigestSize()
    {
        var digest = new string('a', 64);
        using var handler = new ScriptedHandler(_ => ReleaseResponse(Tag,
            (AssetName(GpuVariant.Cpu, OSPlatform.Linux), $"sha256:{digest}", 123L)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var result = await catalog.ResolveAssetAsync(Tag, OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu, CancellationToken.None);

        var asset = AssertEx.NotNull(result.Asset);
        AssertEx.Equal(AssetName(GpuVariant.Cpu, OSPlatform.Linux), asset.Name);
        // The sha256: prefix is stripped.
        AssertEx.Equal(digest, asset.Digest);
        AssertEx.Equal(expected: 123L, asset.Size);
        AssertEx.True(asset.DownloadUrl.IsAbsoluteUri);
    }

    [Test]
    public async Task ResolveAsset_WhenEtagUnchanged_Returns304CachedParseWithoutRefetch()
    {
        var digest = new string('b', 64);
        var bodyCalls = 0;
        using var handler = new ScriptedHandler(request =>
        {
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                // Second call carries the ETag → server says nothing changed; no body is returned.
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            bodyCalls++;
            var response = ReleaseResponse(Tag, (AssetName(GpuVariant.Cpu, OSPlatform.Linux), $"sha256:{digest}", 1L));
            response.Headers.ETag = new EntityTagHeaderValue("\"etag-1\"");
            return response;
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var first = await catalog.ResolveAssetAsync(Tag, OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu, CancellationToken.None);
        var second = await catalog.ResolveAssetAsync(Tag, OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu, CancellationToken.None);

        // The body was parsed exactly once; the 304 reused the cached parse.
        AssertEx.Equal(expected: 1, bodyCalls);
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Equal(digest, AssertEx.NotNull(first.Asset).Digest);
        AssertEx.Equal(digest, AssertEx.NotNull(second.Asset).Digest);
    }

    [Test]
    public async Task ResolveRecommended_WhenOffline_ReturnsNoLiveDataNotThrow()
    {
        using var handler = new ScriptedHandler(_ => throw new HttpRequestException("Simulated network failure."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var result = await catalog.ResolveRecommendedAsync(Tag, CancellationToken.None);

        AssertEx.True(result.IsOffline);
        AssertEx.True(result.HasNoLiveData);
        AssertEx.Null(result.Tag);
    }

    [Test]
    public async Task ResolveRecommended_WhenRateLimited_HonorsRetryAfter()
    {
        var resetEpoch = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        using var handler = new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", resetEpoch.ToString(CultureInfo.InvariantCulture));
            return response;
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var result = await catalog.ResolveRecommendedAsync(Tag, CancellationToken.None);

        AssertEx.True(result.IsRateLimited);
        AssertEx.True(result.HasNoLiveData);
        AssertEx.True(catalog.RateLimitResetUtc.HasValue);
    }

    [Test]
    [Arguments("win", GpuVariant.Cuda)]
    [Arguments("win", GpuVariant.Vulkan)]
    [Arguments("win", GpuVariant.Cpu)]
    [Arguments("ubuntu", GpuVariant.Vulkan)]
    [Arguments("ubuntu", GpuVariant.Cpu)]
    [Arguments("macos", GpuVariant.Cpu)]
    public async Task ResolveAsset_PerOsAndVariant_TemplatesAndMatchesCorrectName(string osToken, GpuVariant variant)
    {
        var (os, arch) = osToken switch
        {
            "win" => (OSPlatform.Windows, Architecture.X64),
            "ubuntu" => (OSPlatform.Linux, Architecture.X64),
            _ => (OSPlatform.OSX, Architecture.Arm64)
        };
        var expectedName = AssetName(variant, os, arch);
        var digest = new string('c', 64);

        using var handler = new ScriptedHandler(_ => ReleaseResponse(Tag, (expectedName, $"sha256:{digest}", 9L)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var result = await catalog.ResolveAssetAsync(Tag, os, arch, variant, CancellationToken.None);

        AssertEx.Equal(expectedName, AssertEx.NotNull(result.Asset).Name);
    }

    [Test]
    public async Task ResolveAsset_WhenCudaVersionDrifts_StillMatchesByTokens()
    {
        // The pin scheme names win-cuda-12.4; upstream may publish cuda-13.3 for a newer tag. Token match must find it.
        const string driftedName = "llama-b9692-bin-win-cuda-13.3-x64.zip";
        var digest = new string('d', 64);
        using var handler = new ScriptedHandler(_ => ReleaseResponse(Tag, (driftedName, $"sha256:{digest}", 5L)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var result = await catalog.ResolveAssetAsync(Tag, OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(driftedName, AssertEx.NotNull(result.Asset).Name);
    }

    [Test]
    [Arguments("../etc/passwd")]
    [Arguments("https://evil.example/x")]
    [Arguments("")]
    [Arguments("latest")]
    [Arguments("b")]
    public async Task ResolveRecommended_RejectsNonBPatternTag(string badTag)
    {
        using var handler = new ScriptedHandler(_ => throw new InvalidOperationException("A malformed tag must never hit the network."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var result = await catalog.ResolveRecommendedAsync(badTag, CancellationToken.None);

        AssertEx.True(result.HasNoLiveData);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task ResolveAsset_WhenAssetNameHasPathSeparator_RejectedNotResolvable()
    {
        // A token-matching name carrying a path separator (a tampered/garbled live value) must be rejected before it can
        // be returned for download — defense-in-depth against path/URL injection from the live API.
        const string tamperedName = "llama-b9692-bin-ubuntu-x64-/.tar.gz";
        var digest = new string('e', 64);
        using var handler = new ScriptedHandler(_ => ReleaseResponse(Tag, (tamperedName, $"sha256:{digest}", 5L)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var result = await catalog.ResolveAssetAsync(Tag, OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Null(result.Asset);
    }

    [Test]
    public async Task ResolveAsset_WhenDigestMissing_FallsThroughNotResolvable()
    {
        // An asset without a digest field is not safely installable → no asset returned (fall to next tier).
        using var handler = new ScriptedHandler(_ => ReleaseResponse(Tag,
            (AssetName(GpuVariant.Cpu, OSPlatform.Linux), Digest: (string?)null, 1L)));
        using var http = new HttpClient(handler, disposeHandler: false);
        var catalog = new GitHubLlamaCppReleaseCatalog(http);

        var result = await catalog.ResolveAssetAsync(Tag, OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Null(result.Asset);
    }

    private static string AssetName(GpuVariant variant, OSPlatform os, Architecture arch = Architecture.X64)
    {
        var pin = LlamaCppReleasePins.Resolve(os, arch, variant);
        return AssertEx.NotNull(pin).AssetName;
    }

    private static HttpResponseMessage ReleaseResponse(string tag, params (string Name, string? Digest, long Size)[] assets)
    {
        var assetJson = string.Join(",", assets.Select(a =>
        {
            var url = $"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{a.Name}";
            var digestField = a.Digest is null ? "null" : $"\"{a.Digest}\"";
            return $$"""{"name":"{{a.Name}}","browser_download_url":"{{url}}","digest":{{digestField}},"size":{{a.Size}}}""";
        }));
        var json = $$"""{"tag_name":"{{tag}}","assets":[{{assetJson}}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }
}
