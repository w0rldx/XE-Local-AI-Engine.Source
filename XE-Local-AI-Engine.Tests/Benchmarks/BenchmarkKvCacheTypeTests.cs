namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkKvCacheTypeTests
{
    [Test]
    [Arguments("f16", "f16")]
    [Arguments("q8_0", "q8_0")]
    [Arguments("q4_0", "q4_0")]
    [Arguments("  Q8_0 ", "q8_0")]
    [Arguments("F16", "f16")]
    public void TryNormalize_AllowedType_IsCanonicalized(string requested, string expected)
    {
        AssertEx.True(BenchmarkKvCacheType.TryNormalize(requested, out var normalized));
        AssertEx.Equal(expected, normalized);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void TryNormalize_NoRequest_IsAutoAndValid(string? requested)
    {
        var accepted = BenchmarkKvCacheType.TryNormalize(requested, out var normalized);

        AssertEx.True(accepted, "a missing KV-cache type is Auto, not a bad request");
        AssertEx.Null(normalized, "Auto is null on the wire and stays null until freeze resolves it");
    }

    [Test]
    [Arguments("q4_k_m")]
    [Arguments("bf16")]
    [Arguments("q8")]
    [Arguments("f16,q8_0")]
    [Arguments("q8_0 q4_0")]
    public void TryNormalize_UnknownType_IsRejected(string requested)
    {
        var accepted = BenchmarkKvCacheType.TryNormalize(requested, out var normalized);

        AssertEx.False(accepted, $"'{requested}' is outside the allow-list and must produce a 400");
        AssertEx.Null(normalized);
    }

    [Test]
    public void Apply_F16_StripsCacheTypesAndKeepsPlacement()
    {
        var frozen = FrozenReplay(kvType: "q8_0", flashAttn: true);

        var applied = BenchmarkKvCacheType.Apply(frozen, BenchmarkKvCacheType.F16);

        AssertEx.Null(applied.KvTypeK, "f16 emits no -ctk");
        AssertEx.Null(applied.KvTypeV, "f16 emits no -ctv");
        AssertEx.False(applied.FlashAttn, "f16 leaves flash attention at the runtime default");
        AssertPlacementPreserved(frozen, applied);
    }

    [Test]
    [Arguments("q8_0")]
    [Arguments("q4_0")]
    public void Apply_QuantizedType_SetsSymmetricCacheTypesWithFlashAttentionAndKeepsPlacement(string type)
    {
        var frozen = FrozenReplay(kvType: null, flashAttn: false);

        var applied = BenchmarkKvCacheType.Apply(frozen, type);

        AssertEx.Equal(type, applied.KvTypeK);
        AssertEx.Equal(type, applied.KvTypeV);
        AssertEx.True(applied.FlashAttn, "a quantized KV cache requires -fa on");
        AssertPlacementPreserved(frozen, applied);
    }

    [Test]
    public void Apply_OverridesAFrozenQuantizedTypeWithoutRefitting()
    {
        var frozen = FrozenReplay(kvType: "q8_0", flashAttn: true);

        var applied = BenchmarkKvCacheType.Apply(frozen, BenchmarkKvCacheType.Q4_0);

        AssertEx.Equal(BenchmarkKvCacheType.Q4_0, applied.KvTypeK);
        AssertEx.Equal(BenchmarkKvCacheType.Q4_0, applied.KvTypeV);
        AssertPlacementPreserved(frozen, applied);
    }

    [Test]
    public void Apply_UnknownTypeOrExploreBase_Throws()
    {
        _ = AssertEx.Throws<ArgumentException>(() =>
            BenchmarkKvCacheType.Apply(FrozenReplay(kvType: null, flashAttn: false), "q4_k_m"));
        _ = AssertEx.Throws<ArgumentException>(() =>
            BenchmarkKvCacheType.Apply(ResolvedLaunchArguments.Explore(), BenchmarkKvCacheType.Q8_0));
    }

    private static ResolvedLaunchArguments FrozenReplay(string? kvType, bool flashAttn) =>
        ResolvedLaunchArguments.Replay(ctxSize: 8192,
            nGpuLayers: 32,
            tensorSplit: "1,0",
            overrideTensor: "exps=CPU",
            kvType,
            kvType,
            flashAttn);

    private static void AssertPlacementPreserved(ResolvedLaunchArguments frozen, ResolvedLaunchArguments applied)
    {
        AssertEx.Equal(frozen.CtxSize, applied.CtxSize);
        AssertEx.Equal(frozen.NGpuLayers, applied.NGpuLayers);
        AssertEx.Equal<string?>(frozen.TensorSplit, applied.TensorSplit);
        AssertEx.Equal<string?>(frozen.OverrideTensor, applied.OverrideTensor);
        AssertEx.False(applied.ExploreMode, "applying a KV-cache type must never return to auto-fit");
    }
}
