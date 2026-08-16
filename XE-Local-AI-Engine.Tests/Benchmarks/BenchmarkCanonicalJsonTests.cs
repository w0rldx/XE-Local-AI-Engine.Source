namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

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

    private sealed record Nested(string? KvType, int Layers);

    private sealed record AscendingOrder(string Backend, int Ctx, Nested Nested);

    private sealed record DescendingOrder(Nested Nested, int Ctx, string Backend);
}
