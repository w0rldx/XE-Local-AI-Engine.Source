namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class BenchmarkCanonicalJsonTests
{
    [Test]
    public void Serialize_MembersDeclaredInDifferentOrders_ProducesTheSameDocument()
    {
        var first = BenchmarkCanonicalJson.Serialize(new AscendingOrder("cuda", Ctx: 8192, Nested: new Nested("q8_0", Layers: 32)));
        var second = BenchmarkCanonicalJson.Serialize(new DescendingOrder(new Nested("q8_0", Layers: 32), Ctx: 8192, "cuda"));

        AssertEx.Equal(first, second, "reordering a receipt's properties must not change its canonical form");
        AssertEx.Equal(BenchmarkCanonicalJson.HashOf(new AscendingOrder("cuda", Ctx: 8192, Nested: new Nested("q8_0", Layers: 32))),
            BenchmarkCanonicalJson.HashOf(new DescendingOrder(new Nested("q8_0", Layers: 32), Ctx: 8192, "cuda")));
    }

    [Test]
    public void Serialize_KeepsNullMembersAndEmitsNoWhitespace()
    {
        var json = BenchmarkCanonicalJson.Serialize(new Nested(KvType: null, Layers: 32));

        AssertEx.Equal("{\"kvType\":null,\"layers\":32}", json);
    }

    [Test]
    public void Hash_IsStableForEqualValuesAndDiffersOnAnyChange()
    {
        var baseline = new AscendingOrder("cuda", Ctx: 8192, Nested: new Nested("q8_0", Layers: 32));
        var changed = baseline with
        {
            Nested = new Nested("q4_0", Layers: 32)
        };

        AssertEx.Equal(BenchmarkCanonicalJson.HashOf(baseline), BenchmarkCanonicalJson.HashOf(baseline));
        AssertEx.NotEqual(BenchmarkCanonicalJson.HashOf(baseline), BenchmarkCanonicalJson.HashOf(changed));
        AssertEx.Equal(expected: 64, BenchmarkCanonicalJson.HashOf(baseline).Length);
    }

    [Test]
    public void Hash_MatchesTheHashOfTheCanonicalText()
    {
        var value = new Nested("q8_0", Layers: 32);

        AssertEx.Equal(BenchmarkCanonicalJson.Hash(BenchmarkCanonicalJson.Serialize(value)), BenchmarkCanonicalJson.HashOf(value));
    }

    [Test]
    public void Serialize_Receipt_WritesEnumsAsNamesSoAnInsertedMemberCannotRelabelStoredEvidence()
    {
        var json = BenchmarkCanonicalJson.Serialize(new LlamaServerLaunchReceipt
        {
            ReceiptVersion = LlamaServerLaunchReceipt.CurrentVersion,
            Variant = GpuVariant.Cuda,
            Os = "linux",
            ExecutableVersion = "b10201",
            ExecutableSha256 = "exe-sha",
            ManifestSha256 = "manifest-sha",
            LaunchProjection = LlamaServerLaunchProjection.From(GpuVariant.Cuda, ResolvedLaunchArguments.Replay(4096), plan: null),
            AuxAssets = new LlamaServerLaunchAuxAssets(false, false, false),
            Placement = new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.None, 0, 33),
            EffectiveContextTokens = 4096,
            BenchmarkLaunchPolicy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
            OmittedOptions = ["--metrics"]
        });

        AssertEx.Contains(json, "\"variant\":\"cuda\"");
        // The frontend walks the decoded receipt generically, so a new member only reaches the UI if it is serialized.
        AssertEx.Contains(json, "\"omittedOptions\":[\"--metrics\"]");
        AssertEx.Contains(json, "\"outcome\":\"none\"");
        AssertEx.False(json.Contains("\"variant\":1", StringComparison.Ordinal),
            "An enum written as its ordinal re-labels every stored receipt the day a member is inserted.");
    }

    private sealed record Nested(string? KvType, int Layers);

    private sealed record AscendingOrder(string Backend, int Ctx, Nested Nested);

    private sealed record DescendingOrder(Nested Nested, int Ctx, string Backend);
}
