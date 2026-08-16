namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the public capability seam a caller outside this provider needs in order to settle a launch vector BEFORE
///     any process exists — it has to be able to reject a KV cache type the selected binary does not accept rather than
///     discover it as a failed spawn. The answers come from the same probed manifest the launch path already gates on,
///     and neither the manifest nor the resolved binary (which carries a filesystem path) crosses the boundary.
/// </summary>
public sealed class LlamaServerLaunchCapabilityInspectorTests
{
    // The shape of the pinned b10201 --help lines the manifest parses its allowed values out of. The "allowed values"
    // line must directly follow its option line — that adjacency is what the manifest grammar keys on.
    private const string Help = """
                                -fa, --flash-attn [on|off|auto]
                                -ctk, --cache-type-k TYPE
                                    allowed values: f32, f16, bf16, q8_0, q4_0, q4_1, iq4_nl, q5_0, q5_1
                                -ctv, --cache-type-v TYPE
                                    allowed values: f32, f16, bf16, q8_0, q4_0, q4_1, iq4_nl, q5_0, q5_1
                                """;

    [Test]
    public async Task InspectAsync_AnswersFromTheProbedManifest()
    {
        var inspector = NewInspector(GpuVariant.Cuda, Help);

        var capabilities = await inspector.InspectAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Cuda, capabilities.Variant);
        AssertEx.True(capabilities.ProbeSucceeded);
        AssertEx.Equal("b10201", capabilities.ExecutableVersion);
        AssertEx.Equal(new string('a', count: 64), capabilities.ManifestSha256);

        AssertEx.True(capabilities.SupportsCacheTypeK("q8_0"));
        AssertEx.True(capabilities.SupportsCacheTypeV("q4_0"));
        AssertEx.True(capabilities.SupportsCacheTypeK("f16"));
        AssertEx.False(capabilities.SupportsCacheTypeK("nvfp4"));
        AssertEx.False(capabilities.SupportsCacheTypeV("nvfp4"));
        AssertEx.True(capabilities.SupportsFlashAttentionMode("on"));
        AssertEx.True(capabilities.SupportsFlashAttentionMode("auto"));
        AssertEx.False(capabilities.SupportsFlashAttentionMode("fused"));
    }

    [Test]
    public async Task InspectAsync_WhenTheProbeFailed_ReportsItRatherThanClaimingNothingIsSupported()
    {
        // "The probe failed" and "the binary rejects q8_0" are different facts and a caller must be able to tell them
        // apart — one is a broken runtime, the other a legitimate 422.
        var binary = new LlamaBinary("/fake/bin/llama-server", "b10201", GpuVariant.Cuda, IsPinnedFallback: true);
        var failed = LlamaServerCapabilityManifest.Failed(binary, executableLengthBytes: 1, DateTimeOffset.UnixEpoch);
        var inspector = new LlamaServerLaunchCapabilityInspector(new FakeVariantSelector(GpuVariant.Cuda),
            new FakeBinaryManager(),
            new FakeLlamaServerCapabilityManifestProbe(failed));

        var capabilities = await inspector.InspectAsync(CancellationToken.None);

        AssertEx.False(capabilities.ProbeSucceeded);
        AssertEx.Null(capabilities.ExecutableVersion);
        AssertEx.Null(capabilities.ManifestSha256);
        AssertEx.False(capabilities.SupportsCacheTypeK("q8_0"));
    }

    [Test]
    public void AddLlamaServerLocalModelProvider_RegistersTheInspector()
    {
        // The whole point of this seam is that another layer can resolve it; a type nobody can obtain is not a seam.
        using var http = new HttpClient();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(http);
        services.AddLlamaServerLocalModelProvider();

        using var provider = services.BuildServiceProvider();

        AssertEx.NotNull(provider.GetRequiredService<ILlamaServerLaunchCapabilityInspector>());
    }

    private static LlamaServerLaunchCapabilityInspector NewInspector(GpuVariant variant, string help)
    {
        var binary = new LlamaBinary("/fake/bin/llama-server", "b10201", variant, IsPinnedFallback: true);
        var manifest = LlamaServerCapabilityManifest.FromSuccessfulProbe(binary,
            executableLengthBytes: 1,
            DateTimeOffset.UnixEpoch,
            new string('a', count: 64),
            "b10201",
            help);

        return new LlamaServerLaunchCapabilityInspector(new FakeVariantSelector(variant),
            new FakeBinaryManager(),
            new FakeLlamaServerCapabilityManifestProbe(manifest));
    }
}
