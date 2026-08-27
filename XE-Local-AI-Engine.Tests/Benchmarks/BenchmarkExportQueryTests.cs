namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkExportQueryTests
{
    private static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid AttemptId = Guid.Parse("20000000-0000-0000-0000-000000000002");

    [Test]
    public async Task GetJsonAsync_ReadsTheFullExportGraphAndOnlyResolvesFactsForMeasuredGroupRepresentatives()
    {
        var groupId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var repeatOne = Run(1, repeatGroupId: groupId, repeatIndex: 1, throughput: new BenchmarkRunThroughput(PromptTokens: 10, PromptMs: 5));
        var repeatZero = Run(2, repeatGroupId: groupId, repeatIndex: 0, throughput: new BenchmarkRunThroughput(PromptTokens: 10, PromptMs: 4));
        var ungrouped = Run(3, throughput: new BenchmarkRunThroughput(GenerationTokens: 10, GenerationMs: 8));
        var warmup = Run(4, isWarmup: true, throughput: new BenchmarkRunThroughput(PromptTokens: 10, PromptMs: 3));
        var unmeasured = Run(5);
        BenchmarkRunRecord[] summaries = [repeatOne, repeatZero, ungrouped, warmup, unmeasured];
        var store = Store(Project(), summaries);
        store.GetJudgeAttemptAsync(AttemptId, Arg.Any<CancellationToken>()).Returns((BenchmarkJudgeAttemptRecord?)null);
        var resolver = Substitute.For<IBenchmarkExportFactsResolver>();
        resolver.ResolveProject(Arg.Any<BenchmarkProjectRecord>()).Returns(new BenchmarkFidelityDisplayFacts("expected"));
        resolver.ResolveRun(Arg.Any<BenchmarkRunRecord>()).Returns(BenchmarkExportRunFacts.Empty);
        var query = new BenchmarkExportQuery(store, resolver);

        var result = AssertEx.NotNull(await query.GetJsonAsync(ProjectId, CancellationToken.None).ConfigureAwait(false));

        AssertEx.Equal(expected: 5, result.Runs.Count);
        AssertEx.Equal("expected", result.Fidelity.ExpectedKldDigest);
        AssertEx.Equal(expected: 2, result.Facts.Count);
        AssertEx.True(result.Facts.ContainsKey(repeatZero.Id));
        AssertEx.True(result.Facts.ContainsKey(ungrouped.Id));
        _ = resolver.Received(1).ResolveRun(Arg.Is<BenchmarkRunRecord>(run => run.Id == repeatZero.Id));
        _ = resolver.Received(1).ResolveRun(Arg.Is<BenchmarkRunRecord>(run => run.Id == ungrouped.Id));
        _ = resolver.DidNotReceive().ResolveRun(Arg.Is<BenchmarkRunRecord>(run => run.Id == repeatOne.Id || run.Id == warmup.Id || run.Id == unmeasured.Id));
        _ = await store.Received(1).GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>()).ConfigureAwait(false);
        _ = await store.Received(1).GetActivePairwiseFitAsync(ProjectId, Arg.Any<CancellationToken>()).ConfigureAwait(false);
        _ = await store.Received(5).GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        _ = await store.Received(1).GetJudgeAttemptAsync(AttemptId, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task GetJsonAsync_PreservesTheStoreRunOrder()
    {
        BenchmarkRunRecord[] summaries = [Run(9), Run(2), Run(7)];
        var store = Store(Project(), summaries);
        var query = new BenchmarkExportQuery(store, Substitute.For<IBenchmarkExportFactsResolver>());

        var result = AssertEx.NotNull(await query.GetJsonAsync(ProjectId, CancellationToken.None).ConfigureAwait(false));

        AssertEx.True(result.Summaries.Select(static run => run.Id).SequenceEqual(summaries.Select(static run => run.Id)));
        AssertEx.True(result.Runs.Select(static item => item.Summary.Id).SequenceEqual(summaries.Select(static run => run.Id)));
    }

    [Test]
    public async Task GetCsvAsync_UsesTheShallowReadGraph()
    {
        BenchmarkRunRecord[] summaries = [Run(1), Run(2)];
        var store = Store(Project(), summaries);
        var resolver = Substitute.For<IBenchmarkExportFactsResolver>();
        resolver.ResolveProject(Arg.Any<BenchmarkProjectRecord>()).Returns(new BenchmarkFidelityDisplayFacts("expected"));
        var query = new BenchmarkExportQuery(store, resolver);

        var result = AssertEx.NotNull(await query.GetCsvAsync(ProjectId, CancellationToken.None).ConfigureAwait(false));

        AssertEx.True(result.Runs.Select(static run => run.Id).SequenceEqual(summaries.Select(static run => run.Id)));
        AssertEx.Equal("expected", result.Fidelity.ExpectedKldDigest);
        _ = await store.DidNotReceive().GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        _ = await store.DidNotReceive().GetJudgeAttemptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        _ = await store.DidNotReceive().GetCurrentJudgePolicyRevisionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        _ = resolver.DidNotReceiveWithAnyArgs().ResolveRun(default!);
    }

    [Test]
    public async Task GetJsonAsync_PropagatesCancellationAndStopsTheReadGraph()
    {
        var cancellation = new CancellationToken(canceled: true);
        var store = Substitute.For<IBenchmarkStore>();
        store.GetProjectAsync(ProjectId, cancellation).Returns(Task.FromCanceled<BenchmarkProjectRecord?>(cancellation));
        var query = new BenchmarkExportQuery(store, Substitute.For<IBenchmarkExportFactsResolver>());

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => query.GetJsonAsync(ProjectId, cancellation)).ConfigureAwait(false);

        _ = await store.Received(1).GetProjectAsync(ProjectId, cancellation).ConfigureAwait(false);
        _ = await store.DidNotReceive().ListAllRunsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public void FidelityDisplayFacts_DerivesTheCurrentComparableDigest()
    {
        var project = Project() with
        {
            FidelityKldEnabled = true,
            FidelityKldBaseFingerprint = "v1:" + new string('a', 64),
            FidelityChunks = 50
        };

        var facts = BenchmarkFidelityDisplayFacts.FromProject(project);

        var expected = BenchmarkKldCacheKey.Create(project.FidelityKldBaseFingerprint,
            BenchmarkFidelityCorpus.Require().Sha256,
            BenchmarkFidelityPolicy.ClampChunks(project.FidelityChunks));
        AssertEx.Equal(expected.Digest, facts.ExpectedKldDigest);
        AssertEx.Null(BenchmarkFidelityDisplayFacts.FromProject(project with
        {
            FidelityKldEnabled = false
        }).ExpectedKldDigest);
    }

    private static IBenchmarkStore Store(BenchmarkProjectRecord project, IReadOnlyList<BenchmarkRunRecord> summaries)
    {
        var store = Substitute.For<IBenchmarkStore>();
        store.GetProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);
        store.ListAllRunsAsync(project.Id, Arg.Any<CancellationToken>()).Returns(new BenchmarkRunPage(summaries, summaries.Count));
        store.GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(call => summaries.SingleOrDefault(run => run.Id == call.ArgAt<Guid>(0)));
        return store;
    }

    private static BenchmarkProjectRecord Project() =>
        new(ProjectId,
            "project",
            Encoding.UTF8.GetBytes("\"task\""),
            4096,
            Guid.Parse("40000000-0000-0000-0000-000000000004"),
            false,
            null,
            false,
            1,
            1,
            1);

    private static BenchmarkRunRecord Run(int ordinal,
        Guid? repeatGroupId = null,
        int? repeatIndex = null,
        bool isWarmup = false,
        BenchmarkRunThroughput? throughput = null)
    {
        var id = Guid.Parse($"50000000-0000-0000-0000-{ordinal:D12}");
        return new BenchmarkRunRecord(id,
            ProjectId,
            Encoding.UTF8.GetBytes("{}"),
            $"model-{ordinal}",
            LocalModelOrigin.Imported,
            $"fingerprint-{ordinal}",
            "agent",
            1,
            4096,
            BenchmarkPrimaryStatus.Succeeded,
            4096,
            10,
            10,
            1,
            Encoding.UTF8.GetBytes("[]"),
            0,
            null,
            null,
            1,
            ordinal,
            ordinal,
            ordinal,
            ordinal,
            Judge: ordinal == 1 ? new BenchmarkRunJudgeView("succeeded", AttemptId, 80, 1, null, 1, 1, "key", null, true, true, null) : null,
            Throughput: throughput,
            RepeatGroupId: repeatGroupId,
            RepeatIndex: repeatIndex,
            IsWarmup: isWarmup);
    }
}
