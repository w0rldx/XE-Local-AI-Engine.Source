namespace XE_Local_AI_Engine.Tests.ModelFit;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The memo behind "Refresh now": the six per-use-case runs share one Hugging Face search per term and one
///     inspection per repo, a failure is retried, and nothing outlives the reuse window.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class RecommendationDiscoveryMemoTests
{
    private static readonly GgufSearchQuery InstructTrending = new()
    {
        SearchText = "instruct",
        Limit = 12,
        Sort = GgufSearchSort.Trending
    };

    [Test]
    public async Task ConcurrentRuns_ShareOneSearchAndOneInspection()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<GgufRepoSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = Substitute.For<IHuggingFaceGgufDiscovery>();
        inner.SearchAsync(InstructTrending, Arg.Any<CancellationToken>()).Returns(gate.Task);
        inner.InspectRepoAsync("owner/repo", Arg.Any<CancellationToken>()).Returns(Detail("owner/repo"));
        var memo = new RecommendationDiscoveryMemo(inner, new ManualClock());

        var searches = Enumerable.Range(0, 3).Select(_ => memo.SearchAsync(InstructTrending with { }, CancellationToken.None)).ToList();
        gate.SetResult([]);
        await Task.WhenAll(searches);
        await memo.InspectRepoAsync("owner/repo", CancellationToken.None);
        await memo.InspectRepoAsync("OWNER/repo", CancellationToken.None);

        await inner.Received(1).SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>());
        await inner.Received(1).InspectRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailedInspection_IsRetried_AndAReadingExpiresAfterTheWindow()
    {
        var inner = Substitute.For<IHuggingFaceGgufDiscovery>();
        inner.InspectRepoAsync("owner/repo", Arg.Any<CancellationToken>())
             .Returns(_ => Task.FromException<GgufRepoDetail>(new HttpRequestException("hub down")), _ => Detail("owner/repo"));
        var clock = new ManualClock();
        var memo = new RecommendationDiscoveryMemo(inner, clock);

        await AssertEx.ThrowsAsync<HttpRequestException>(() => memo.InspectRepoAsync("owner/repo", CancellationToken.None));
        await memo.InspectRepoAsync("owner/repo", CancellationToken.None);
        await memo.InspectRepoAsync("owner/repo", CancellationToken.None);
        await inner.Received(2).InspectRepoAsync("owner/repo", Arg.Any<CancellationToken>());

        clock.Advance(RecommendationDiscoveryMemo.ReuseWindow);
        await memo.InspectRepoAsync("owner/repo", CancellationToken.None);

        await inner.Received(3).InspectRepoAsync("owner/repo", Arg.Any<CancellationToken>());
    }

    private static Task<GgufRepoDetail> Detail(string repoId)
    {
        return Task.FromResult(new GgufRepoDetail
        {
            RepoId = repoId,
            IsGated = false,
            License = null,
            Files = []
        });
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
        }
    }
}
