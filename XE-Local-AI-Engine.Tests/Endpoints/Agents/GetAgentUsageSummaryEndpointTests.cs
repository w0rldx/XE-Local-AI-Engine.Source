namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>GET agents/usage-summary</c> end to end: the query-string range reaches the real SQL aggregate over
///     <c>agent_execution_logs</c>, whose buckets are folded by the real mapper and priced by the real rate resolver.
///     The aggregate's own arithmetic is covered in the persistence suite and the pricing maths in
///     <c>UsageSummaryMapperTests</c>; what only an HTTP test can prove is that the two bounds bind and are applied in
///     the right direction, that the wire envelope carries items, totals, the per-provider rollup and the retention
///     window, and that cloud runs are actually priced rather than reported free.
///     <para>
///         A dedicated class because the host's database is shared by the class and a rangeless request sums every row
///         in it. Isolation is by construction rather than by order: each test seeds a disjoint, far-future day window
///         (the retention sweep only deletes rows OLDER than its horizon, so a future row is never swept) under model
///         names carrying a fresh <see cref="Guid" />, and queries bounded by that window.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GetAgentUsageSummaryEndpointTests
{
    private const long MillisecondsPerDay = 86_400_000L;

    // Disjoint day windows, one block per test, far enough apart that no test's range can reach another's rows.
    private const long RangelessDay = 40_000L;
    private const long HalfOpenDay = 40_010L;
    private const long GroupingDay = 40_020L;
    private const long TokenSumDay = 40_030L;
    private const long RollupDay = 40_040L;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task UsageSummary_WithNoRange_SummarizesEveryRetainedRowAndReportsTheRetentionWindow()
    {
        // The rangeless read is the SPA's default, and the retention window is the only thing telling a reader that
        // "all time" means the retained horizon rather than the node's whole history.
        var modelName = UniqueModelName();
        await SeedEnvelopeAsync(modelName, AgentUsageProviders.Local, DayStart(RangelessDay) + 10, prompt: 13, completion: 17, reasoning: 19, total: 49);

        var payload = await GetSummaryAsync(fromEpochMs: null, toEpochMs: null);

        var bucket = AssertEx.NotNull(payload.Items.SingleOrDefault(item => item.ModelName == modelName),
            "A rangeless summary must include the seeded row.");
        AssertEx.Equal(expected: 1, bucket.RunCount);
        AssertEx.Equal(expected: 13L, bucket.PromptTokens);
        AssertEx.Equal(DayStart(RangelessDay), bucket.DayStartUtcMs);

        // Read from the host rather than hard-coded: the claim is that the endpoint reports the CONFIGURED window, not
        // that this build's default happens to be some number.
        var configured = Factory.Services.GetRequiredService<IOptions<AgentExecutionLogRetentionOptions>>().Value.RetentionDays;
        AssertEx.Equal(configured, payload.RetentionDays);
    }

    [Test]
    public async Task UsageSummary_AppliesTheRangeHalfOpen_IncludingTheLowerBoundAndExcludingTheUpper()
    {
        // Both bounds bind from the query string, and the direction matters: an inclusive upper bound would double-count
        // the boundary row in two adjacent windows, which is exactly how a per-day report starts disagreeing with itself.
        var modelName = UniqueModelName();
        var from = DayStart(HalfOpenDay);
        var to = DayStart(HalfOpenDay + 1);
        await SeedEnvelopeAsync(modelName, AgentUsageProviders.Local, from, prompt: 1, completion: 1, reasoning: 0, total: 2);
        await SeedEnvelopeAsync(modelName, AgentUsageProviders.Local, from + 100, prompt: 10, completion: 10, reasoning: 0, total: 20);
        await SeedEnvelopeAsync(modelName, AgentUsageProviders.Local, to, prompt: 999, completion: 999, reasoning: 0, total: 1_998);

        var payload = await GetSummaryAsync(from, to);

        AssertEx.Equal(expected: 1, payload.Items.Count, "The window holds one day and one (model, provider) pair.");
        var bucket = payload.Items[0];
        AssertEx.Equal(modelName, bucket.ModelName);
        AssertEx.Equal(expected: 2, bucket.RunCount);
        // 11, not 1010: the row sitting exactly on the upper bound is outside the window.
        AssertEx.Equal(expected: 11L, bucket.PromptTokens);
        AssertEx.Equal(expected: 22L, bucket.TotalTokens);
    }

    [Test]
    public async Task UsageSummary_GroupsByModelProviderAndUtcDay_NewestDayFirst()
    {
        // The group key is a triple, and the wire order is what the SPA renders without re-sorting: newest day first,
        // then provider, then model name.
        var prefix = UniqueModelName();
        var modelA = $"{prefix}-a";
        var modelB = $"{prefix}-b";
        var from = DayStart(GroupingDay);
        var to = DayStart(GroupingDay + 2);
        await SeedEnvelopeAsync(modelA, AgentUsageProviders.Local, DayStart(GroupingDay + 1) + 5, prompt: 7, completion: 0, reasoning: 0, total: 7);
        await SeedEnvelopeAsync(modelA, AgentUsageProviders.Local, from + 10, prompt: 1, completion: 0, reasoning: 0, total: 1);
        await SeedEnvelopeAsync(modelB, AgentUsageProviders.Local, from + 20, prompt: 2, completion: 0, reasoning: 0, total: 2);
        await SeedEnvelopeAsync(modelA, AgentUsageProviders.Ollama, from + 30, prompt: 3, completion: 0, reasoning: 0, total: 3);

        var payload = await GetSummaryAsync(from, to);

        AssertEx.Equal(expected: 4, payload.Items.Count, "Two days x (model, provider) pairs must not fold together.");
        AssertEx.Equal(DayStart(GroupingDay + 1), payload.Items[0].DayStartUtcMs);
        AssertEx.Equal(modelA, payload.Items[0].ModelName);

        AssertEx.Equal(DayStart(GroupingDay), payload.Items[1].DayStartUtcMs);
        AssertEx.Equal(AgentUsageProviders.Local, payload.Items[1].Provider);
        AssertEx.Equal(modelA, payload.Items[1].ModelName);

        AssertEx.Equal(AgentUsageProviders.Local, payload.Items[2].Provider);
        AssertEx.Equal(modelB, payload.Items[2].ModelName);

        // Same model and same day as Items[1], different provider: the provider is part of the key, not a label.
        AssertEx.Equal(AgentUsageProviders.Ollama, payload.Items[3].Provider);
        AssertEx.Equal(modelA, payload.Items[3].ModelName);
        AssertEx.Equal(DayStart(GroupingDay), payload.Items[3].DayStartUtcMs);
    }

    [Test]
    public async Task UsageSummary_SumsEveryTokenColumnPerBucket_CountingAnUnreportedColumnAsZero()
    {
        // All four token columns are nullable, and a run that reported no usage must contribute 0 rather than drop the
        // bucket or null the sum — the arithmetic runs inside SQLite, where a NULL would poison the whole SUM.
        var modelName = UniqueModelName();
        var from = DayStart(TokenSumDay);
        var to = DayStart(TokenSumDay + 1);
        await SeedEnvelopeAsync(modelName, AgentUsageProviders.Local, from + 10, prompt: 100, completion: 200, reasoning: 50, total: 350);
        await SeedEnvelopeAsync(modelName, AgentUsageProviders.Local, from + 20, prompt: 5, completion: 6, reasoning: null, total: null);

        var payload = await GetSummaryAsync(from, to);

        AssertEx.Equal(expected: 1, payload.Items.Count);
        var bucket = payload.Items[0];
        AssertEx.Equal(expected: 2, bucket.RunCount);
        AssertEx.Equal(expected: 105L, bucket.PromptTokens);
        AssertEx.Equal(expected: 206L, bucket.CompletionTokens);
        AssertEx.Equal(expected: 50L, bucket.ReasoningTokens);
        AssertEx.Equal(expected: 350L, bucket.TotalTokens);
    }

    [Test]
    public async Task UsageSummary_RollsUpByProvider_PricingCloudRunsAndKeepingLocalRuntimesFree()
    {
        // The rollup is folded from the same buckets, biggest consumer first, and the cost is resolved through the real
        // IUsageRateResolver: a local run must stay at zero however many tokens it burned, and a hosted model must not.
        var localModel = UniqueModelName();
        var from = DayStart(RollupDay);
        var to = DayStart(RollupDay + 1);
        await SeedEnvelopeAsync(localModel, AgentUsageProviders.Local, from + 10, prompt: 1_000, completion: 0, reasoning: 0, total: 1_000);
        await SeedEnvelopeAsync(localModel, AgentUsageProviders.Ollama, from + 20, prompt: 10, completion: 0, reasoning: 0, total: 10);
        await SeedEnvelopeAsync("gpt-4o-mini", AgentUsageProviders.Codex, from + 30, prompt: 1_000_000, completion: 0, reasoning: 0, total: 1_000_000);

        var payload = await GetSummaryAsync(from, to);

        AssertEx.Equal(expected: 3, payload.ByProvider.Count);
        // Ordered by descending total tokens: codex (1,000,000) then local (1,000) then ollama (10).
        AssertEx.Equal(AgentUsageProviders.Codex, payload.ByProvider[0].Provider);
        AssertEx.Equal(AgentUsageProviders.Local, payload.ByProvider[1].Provider);
        AssertEx.Equal(AgentUsageProviders.Ollama, payload.ByProvider[2].Provider);

        var cloud = payload.ByProvider[0];
        AssertEx.Equal(expected: 1, cloud.RunCount);
        AssertEx.Equal(expected: 1_000_000L, cloud.PromptTokens);
        AssertEx.True(cloud.EstimatedCostUsd > 0,
            "A priced hosted model must resolve to a non-zero cost, or the rate resolver is not wired into the endpoint.");
        AssertEx.Equal("USD", cloud.Currency);

        AssertEx.Equal(expected: 0d, payload.ByProvider[1].EstimatedCostUsd, "A local runtime is free regardless of token volume.");
        AssertEx.Equal(expected: 0d, payload.ByProvider[2].EstimatedCostUsd, "Ollama is a local runtime and also free.");

        AssertEx.Equal(expected: 3, payload.Totals.RunCount);
        AssertEx.Equal(expected: 1_001_010L, payload.Totals.PromptTokens);
        AssertEx.Equal(expected: 1_001_010L, payload.Totals.TotalTokens);
        // The only priced provider in the window, so the grand total is its cost exactly.
        AssertEx.Equal(cloud.EstimatedCostUsd, payload.Totals.EstimatedCostUsd);
    }

    [Test]
    public async Task UsageSummary_IsOperatorGated()
    {
        // Proven with all three principals: an anonymous 401 alone would stay green if the operator policy were
        // downgraded to plain authentication, and the operator control keeps the 403 from passing vacuously.
        using var client = Factory.CreateClient();

        using var anonymous = Request(authorize: null);
        using var anonymousResponse = await client.SendAsync(anonymous);
        AssertEx.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var forbidden = Request(Factory.AddNonOperatorBearerToken);
        using var forbiddenResponse = await client.SendAsync(forbidden);
        AssertEx.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

        using var allowed = Request(Factory.AddNodeBearerToken);
        using var allowedResponse = await client.SendAsync(allowed);
        AssertEx.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
    }

    private static long DayStart(long dayIndex) =>
        dayIndex * MillisecondsPerDay;

    private static string UniqueModelName() =>
        $"xe-usage-{Guid.NewGuid():N}";

    // The loopback Host/Origin guard rejects an API request without an Origin, so every request carries one — the
    // shape the neighbouring run-envelope tests use.
    private static HttpRequestMessage Request(Action<HttpRequestMessage>? authorize, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/agents/usage-summary{query}");
        request.Headers.Add("Origin", "http://localhost");
        authorize?.Invoke(request);
        return request;
    }

    private async Task<AgentUsageSummaryResponse> GetSummaryAsync(long? fromEpochMs, long? toEpochMs)
    {
        var bounds = string.Join('&',
            new[]
            {
                fromEpochMs is { } from ? $"fromEpochMs={from}" : null,
                toEpochMs is { } to ? $"toEpochMs={to}" : null
            }.OfType<string>());

        using var client = Factory.CreateClient();
        using var request = Request(Factory.AddNodeBearerToken, bounds.Length == 0 ? string.Empty : $"?{bounds}");

        using var response = await client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, responseText);
        return AssertEx.NotNull(JsonSerializer.Deserialize<AgentUsageSummaryResponse>(responseText, JsonOptions));
    }

    // Seeded through raw parameterized SQL rather than the DbSet: the run-envelope entity type is internal to the
    // persistence assembly and no store API writes one (the real write is the terminalize command's own statement,
    // covered separately). Unlike the neighbouring run-envelope seeds this one sets the provider and the four token
    // columns explicitly, because they are the group key and the sums under test. Every value is a bound parameter.
    private async Task SeedEnvelopeAsync(string modelName,
        string provider,
        long createdAtUtc,
        int? prompt,
        int? completion,
        int? reasoning,
        int? total)
    {
        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        _ = await dbContext.Database.ExecuteSqlAsync($"""
                                                      INSERT INTO agent_execution_logs
                                                          (id, record_kind, schema_version, agent_definition_id, conversation_id, message_id, invocation_id,
                                                           model_name, provider, config_hash, terminal_status, latency_ms, success, created_at_utc,
                                                           prompt_tokens, completion_tokens, reasoning_tokens, total_tokens)
                                                      VALUES ({Guid.NewGuid()}, {(int)AgentExecutionLogRecordKind.ChatRunEnvelope}, {AgentRunEnvelope.CurrentSchemaVersion},
                                                              {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()},
                                                              {modelName}, {provider}, '', 'completed', 1500, 1, {createdAtUtc},
                                                              {prompt}, {completion}, {reasoning}, {total});
                                                      """);
    }
}
