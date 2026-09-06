namespace XE_Local_AI_Engine.Tests.Testing;

using System.Globalization;

/// <summary>
///     Covers the retry contract of <see cref="LoopbackPort.BindWithRetryAsync{T}" />: a candidate the
///     product reports as in use is abandoned for a fresh one, and exhausting the attempts is a failure
///     rather than a silent fallback.
/// </summary>
public sealed class LoopbackPortTests
{
    [Test]
    public async Task BindWithRetryAsync_WhenTheFirstCandidatesAreInUse_OffersFreshOnesAndReturnsTheWinner()
    {
        var offered = new List<int>();

        var bound = await LoopbackPort.BindWithRetryAsync(port =>
        {
            offered.Add(port);
            return Task.FromResult(offered.Count < 3 ? null : $"bound:{port}");
        }).ConfigureAwait(false);

        AssertEx.Equal($"bound:{offered[2]}", bound);
        AssertEx.Equal(expected: 3, offered.Count);
        AssertEx.Equal(expected: 3, offered.Distinct().Count(),
            "Every retry must reserve a fresh candidate rather than re-offering the port that was just refused.");
    }

    [Test]
    public async Task BindWithRetryAsync_WhenEveryCandidateIsInUse_ThrowsNamingThePortsTried()
    {
        var offered = new List<int>();

        var failure = await AssertEx.ThrowsAsync<InvalidOperationException>(() => LoopbackPort.BindWithRetryAsync<string>(port =>
        {
            offered.Add(port);
            return Task.FromResult<string?>(null);
        }, maxAttempts: 3)).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, offered.Count);
        foreach (var port in offered)
        {
            AssertEx.Contains(failure.Message, port.ToString(CultureInfo.InvariantCulture));
        }
    }
}
