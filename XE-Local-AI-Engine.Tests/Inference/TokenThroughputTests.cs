namespace XE_Local_AI_Engine.Tests.Inference;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one tokens-per-second derivation four measurement paths now share. The guards are the point: the copies this
///     replaced each carried their own, and a rate reported as infinity — or as zero — for a duration nobody measured
///     is worse than an empty column, because it ranks.
/// </summary>
public sealed class TokenThroughputTests
{
    [Test]
    public void FromMilliseconds_DerivesTheRateAndRefusesAnUnmeasuredDuration()
    {
        AssertEx.Equal<double?>(expected: 40d, TokenThroughput.FromMilliseconds(tokens: 100, milliseconds: 2500));
        AssertEx.Null(TokenThroughput.FromMilliseconds(tokens: null, milliseconds: 2500), "no token count is no rate");
        AssertEx.Null(TokenThroughput.FromMilliseconds(tokens: 100, milliseconds: null), "no duration is no rate");
        AssertEx.Null(TokenThroughput.FromMilliseconds(tokens: 100, milliseconds: 0), "a zero duration must not become infinity");
        AssertEx.Null(TokenThroughput.FromMilliseconds(tokens: 100, milliseconds: -1), "a negative duration is a misread, not a rate");
        AssertEx.Equal<double?>(expected: 0d, TokenThroughput.FromMilliseconds(tokens: 0, milliseconds: 2500),
            "zero tokens in a real duration IS a measurement: the model produced nothing.");
    }

    [Test]
    public void FromSeconds_AppliesTheSameGuardsInTheCounterUnit()
    {
        AssertEx.Equal<double?>(expected: 40d, TokenThroughput.FromSeconds(tokens: 100, seconds: 2.5));
        AssertEx.Null(TokenThroughput.FromSeconds(tokens: null, seconds: 2.5));
        AssertEx.Null(TokenThroughput.FromSeconds(tokens: 100, seconds: null));
        AssertEx.Null(TokenThroughput.FromSeconds(tokens: 100, seconds: 0), "a zero counter delta must not become infinity");
    }
}
