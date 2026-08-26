namespace XE_Local_AI_Engine.Tests.Endpoints.Benchmarks.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The compare read path (B3). The store is a substitute, so every test here states the cell table it is reading
///     — which is the only input the paired delta has.
/// </summary>
public sealed class BenchmarkCompareEndpointTests
{
    private const string Api = "/api/local/v1/benchmarks";
    private static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid AgentId = Guid.Parse("20000000-0000-0000-0000-000000000002");

    [Test]
    public async Task Compare_WithoutOperatorToken_IsUnauthorized()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + $"/projects/{ProjectId}/compare?cellKeys=a&cellKeys=b");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Compare_TwoCells_ReturnsBothAndTheirPairedDelta()
    {
        // A holds a flat +6 over B on all four shared items, so every resample of the paired differences means +6:
        // the interval is degenerate and the two cells ARE separated.
        await using var context = Seeded([
            Cell("cell:one", 76, [Item(0, 76), Item(1, 66), Item(2, 86), Item(3, 56)]),
            Cell("cell:two", 70, [Item(0, 70), Item(1, 60), Item(2, 80), Item(3, 50)])]);

        var (status, content) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:two").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        AssertEx.Equal(2, root.GetProperty("cells").GetArrayLength());
        var delta = root.GetProperty("pairedDeltas")[0];
        AssertEx.Equal("cell:one", delta.GetProperty("aCellKey").GetString());
        AssertEx.Equal("cell:two", delta.GetProperty("bCellKey").GetString());
        AssertEx.Equal(4, delta.GetProperty("sharedItemCount").GetInt32());
        AssertEx.Equal(6d, delta.GetProperty("delta").GetDouble());
        AssertEx.Equal(6d, delta.GetProperty("ciLow").GetDouble());
        AssertEx.Equal(6d, delta.GetProperty("ciHigh").GetDouble());
        AssertEx.True(delta.GetProperty("separated").GetBoolean(), "a gap held on every item is a separation");
    }

    [Test]
    public async Task Compare_ExcludedRuns_LeaveTheirItemsOutOfTheSharedSet()
    {
        // Item 3 was excluded in A (item-revised) and item 0 in B. A shared item needs a rankable score on BOTH
        // sides, so the comparison falls to the two items that have one — and two cannot support an interval.
        await using var context = Seeded([
            Cell("cell:one", null, [Item(0, 76), Item(1, 66), Item(2, 86), Item(3, null, "item-revised")]),
            Cell("cell:two", null, [Item(0, null, "item-revised"), Item(1, 60), Item(2, 80), Item(3, 50)])]);

        var (status, content) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:two").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        // The cells are still returned with their own exclusion reasons: the refusal is legible, not a bare absence.
        AssertEx.Equal(2, root.GetProperty("cells").GetArrayLength());
        AssertEx.Equal(0, root.GetProperty("pairedDeltas").GetArrayLength());
    }

    [Test]
    public async Task Compare_ACellExcludedWholesale_ProducesNoDelta()
    {
        // An item-set-revised cell carries no quality on any run: it was measured against a suite the project no
        // longer has, and a delta against it would be a difference between two different questions.
        await using var context = Seeded([
            Cell("cell:one", 76, [Item(0, 76), Item(1, 66), Item(2, 86)]),
            Cell("cell:two", null, [Item(0, null), Item(1, null), Item(2, null)], "item-set-revised")]);

        var (status, content) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:two").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        AssertEx.Equal(0, root.GetProperty("pairedDeltas").GetArrayLength());
        AssertEx.Equal("item-set-revised", root.GetProperty("cells")[1].GetProperty("rankExclusionReason").GetString());
    }

    [Test]
    public async Task Compare_ThreeCells_ReportsEveryUnorderedPairOnce()
    {
        await using var context = Seeded([
            Cell("cell:one", 80, [Item(0, 80), Item(1, 80), Item(2, 80)]),
            Cell("cell:two", 70, [Item(0, 70), Item(1, 70), Item(2, 70)]),
            Cell("cell:three", 60, [Item(0, 60), Item(1, 60), Item(2, 60)])]);

        var (status, content) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:two&cellKeys=cell:three").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(content);
        var deltas = document.RootElement.GetProperty("pairedDeltas");
        AssertEx.Equal(3, deltas.GetArrayLength());
        AssertEx.Equal(10d, deltas[0].GetProperty("delta").GetDouble());
        AssertEx.Equal(20d, deltas[1].GetProperty("delta").GetDouble());
        AssertEx.Equal(10d, deltas[2].GetProperty("delta").GetDouble());
    }

    [Test]
    public async Task Compare_TwoCellsThatTie_IsNotSeparated()
    {
        await using var context = Seeded([
            Cell("cell:one", 70, [Item(0, 80), Item(1, 70), Item(2, 60)]),
            Cell("cell:two", 70, [Item(0, 60), Item(1, 70), Item(2, 80)])]);

        var (status, content) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:two").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(content);
        var delta = document.RootElement.GetProperty("pairedDeltas")[0];
        AssertEx.Equal(0d, delta.GetProperty("delta").GetDouble());
        AssertEx.False(delta.GetProperty("separated").GetBoolean(), "0 inside the interval is 'not separated by this suite'");
    }

    [Test]
    public async Task Compare_ADisplayOnlyLeaf_IsNotInTheDelta()
    {
        // Item 3 is a NIAH case: judged, scored, and excluded from every quality aggregate. Both cells answered it,
        // and A "wins" it by 40 - which must not move the delta at all. The other three items are a flat +6.
        await using var context = Seeded(
        [
            Cell("cell:one", 76, [Item(0, 76), Item(1, 66), Item(2, 86), Item(3, 90)]),
            Cell("cell:two", 70, [Item(0, 70), Item(1, 60), Item(2, 80), Item(3, 50)])
        ], displayOnlyIndexes: 3);

        var (status, content) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:two").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(content);
        var delta = document.RootElement.GetProperty("pairedDeltas")[0];
        AssertEx.Equal(3, delta.GetProperty("sharedItemCount").GetInt32());
        AssertEx.Equal(6d, delta.GetProperty("delta").GetDouble());
        AssertEx.Equal(6d, delta.GetProperty("ciHigh").GetDouble());
    }

    [Test]
    [Arguments("cellKeys=cell:one")]
    [Arguments("")]
    [Arguments("cellKeys=a&cellKeys=b&cellKeys=c&cellKeys=d&cellKeys=e&cellKeys=f&cellKeys=g")]
    public async Task Compare_OutsideTwoToSixCells_Is400(string query)
    {
        await using var context = Seeded([Cell("cell:one", 70, [Item(0, 70)])]);

        var (status, content) = await GetAsync(context, query).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Contains(content, "Provide between 2 and 6 cellKeys", StringComparison.Ordinal);
    }

    [Test]
    public async Task Compare_WithADuplicateCellKey_Is400()
    {
        await using var context = Seeded([Cell("cell:one", 70, [Item(0, 70), Item(1, 70), Item(2, 70)])]);

        var (status, content) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:one").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Contains(content, "must be distinct", StringComparison.Ordinal);
    }

    [Test]
    public async Task Compare_WithAnUnknownCellKey_Is400_NamingIt()
    {
        await using var context = Seeded([Cell("cell:one", 70, [Item(0, 70), Item(1, 70), Item(2, 70)])]);

        var (status, content) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:gone").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Contains(content, "cell:gone", StringComparison.Ordinal);
    }

    [Test]
    public async Task Compare_ForAnUnknownProject_Is404()
    {
        await using var context = new Context();

        var (status, _) = await GetAsync(context, "cellKeys=cell:one&cellKeys=cell:two").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, status);
    }

    /// <param name="displayOnlyIndexes">Item indexes whose leaf does NOT count toward the score, as a NIAH case does not.</param>
    private static Context Seeded(BenchmarkCellRecord[] cells, params int[] displayOnlyIndexes)
    {
        var context = new Context();
        var indexes = cells.SelectMany(static cell => cell.Items)
                           .Select(static item => item.TaskItemIndex ?? 0)
                           .Distinct()
                           .Order()
                           .ToArray();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project());
        context.Store.ListTaskItemsAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns([.. indexes.Select(index => TaskItem(index, !displayOnlyIndexes.Contains(index)))]);
        context.Store.ListCellsAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkCellPage(cells,
                   new BenchmarkRankCohort(2, "cohort-key", 3, cells.Length, cells.Length),
                   ScorableItemCount: indexes.Length - displayOnlyIndexes.Length));
        return context;
    }

    private static BenchmarkTaskItemRecord TaskItem(int index, bool countsTowardScore) =>
        new(ItemId(index), ProjectId, ParentItemId: null, index, BenchmarkTaskItemKinds.Prompt, Revision: 1, "v1:hash",
            countsTowardScore, Encoding.UTF8.GetBytes("\"ask\""), null, null, null, Version: 1, CreatedAtUtc: 10, UpdatedAtUtc: 20);

    private static async Task<(HttpStatusCode Status, string Content)> GetAsync(Context context, string query)
    {
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + $"/projects/{ProjectId}/compare?{query}");
        context.Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static BenchmarkCellRecord Cell(string key, int? quality, IReadOnlyList<BenchmarkCellItemRecord> items, string? exclusion = null) =>
        new(key, "model.gguf", "v1:fp", "q8_0", null, null, quality, quality is null ? null : 1, exclusion, items);

    /// <summary>One item's answer. A null quality is what the ranking writes for a run it excluded.</summary>
    private static BenchmarkCellItemRecord Item(int index, int? quality, string? exclusion = null) =>
        new(Guid.NewGuid(), ItemId(index), index, quality, "stop", exclusion);

    private static Guid ItemId(int index) =>
        new(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private static BenchmarkProjectRecord Project() =>
        new(ProjectId, "Project", Encoding.UTF8.GetBytes("\"Answer exactly.\""), 4096, AgentId, JudgeEnabled: false,
            CurrentJudgePolicyRevisionId: null, IsFrozen: true, Version: 4, CreatedAtUtc: 10, UpdatedAtUtc: 20);

    private sealed class Context : IAsyncDisposable
    {
        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();

        public TestServerWebAppFactory Factory { get; }

        public Context()
        {
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IBenchmarkStore>();
                    services.AddSingleton(Store);
                }
            };
        }

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }
}
