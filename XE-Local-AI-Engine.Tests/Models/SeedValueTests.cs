namespace XE_Local_AI_Engine.Tests.Models;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for <see cref="SeedValue" />, the shared wire-seed parser. Seeds ride the wire as strings so a
///     64-bit value above 2^53 survives exactly; these tests pin the exact round-trip and the malformed-value rejection
///     that back the chat + image seed boundaries (Blocker 3).
/// </summary>
public sealed class SeedValueTests
{
    [Test]
    public void TryParse_LargeSeedAbove2Pow53_RoundTripsExactly()
    {
        // 9007199254740993 = 2^53 + 1: the first integer a JSON number (IEEE-754 double) cannot represent.
        const string raw = "9007199254740993";

        var parsed = SeedValue.TryParse(raw, out var seed, out var error);

        AssertEx.True(parsed);
        AssertEx.Null(error);
        AssertEx.True(seed.HasValue);
        AssertEx.Equal(9007199254740993L, seed!.Value);
        AssertEx.Equal(raw, SeedValue.ToWire(seed.Value));
    }

    [Test]
    public void TryParse_Int64Bounds_RoundTripExactly()
    {
        AssertEx.True(SeedValue.TryParse("9223372036854775807", out var max, out _));
        AssertEx.Equal(long.MaxValue, max!.Value);

        AssertEx.True(SeedValue.TryParse("-9223372036854775808", out var min, out _));
        AssertEx.Equal(long.MinValue, min!.Value);
    }

    [Test]
    public void TryParse_BlankOrNull_IsValidNoSeed()
    {
        AssertEx.True(SeedValue.TryParse(null, out var fromNull, out var nullError));
        AssertEx.Null(fromNull);
        AssertEx.Null(nullError);

        AssertEx.True(SeedValue.TryParse("   ", out var fromBlank, out _));
        AssertEx.Null(fromBlank);
    }

    [Test]
    public void TryParse_RandomSentinel_ParsesToMinusOne()
    {
        AssertEx.True(SeedValue.TryParse("-1", out var seed, out _));
        AssertEx.Equal(expected: -1L, seed!.Value);
    }

    [Test]
    public void TryParse_NonInteger_IsRejectedWithMessage()
    {
        var parsed = SeedValue.TryParse("not-a-number", out var seed, out var error);

        AssertEx.True(!parsed);
        AssertEx.True(!seed.HasValue);
        AssertEx.Equal(SeedValue.ValidationMessage, error);
        AssertEx.True(!SeedValue.IsValid("12.5"));
        AssertEx.True(!SeedValue.IsValid("9223372036854775808")); // long.MaxValue + 1 overflows.
    }
}
