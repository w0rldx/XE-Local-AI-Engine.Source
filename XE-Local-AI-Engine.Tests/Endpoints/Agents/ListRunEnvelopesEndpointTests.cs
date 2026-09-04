namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The wire projection of the two tool-schema token columns. The store's own projection is covered in the
///     persistence suite; this pins the half a reader actually consumes — a row that carries the estimate returns both
///     numbers over HTTP, and a row that does not returns nulls rather than zeros.
/// </summary>
public sealed class ListRunEnvelopesEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task ListRunEnvelopes_ReturnsTheToolSchemaTokenEstimate_AndNullWhenNotReported()
    {
        var conversationId = Guid.NewGuid();
        var measured = await SeedEnvelopeAsync(conversationId, toolSchemaTokens: (long)int.MaxValue + 1, maxToolSchemaTokens: 4_096).ConfigureAwait(false);
        var unmeasured = await SeedEnvelopeAsync(conversationId, toolSchemaTokens: null, maxToolSchemaTokens: null).ConfigureAwait(false);

        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/agents/run-envelopes?conversationId={conversationId}");
        Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var payload = AssertEx.NotNull(await JsonSerializer.DeserializeAsync<ListRunEnvelopesResponse>(stream, JsonOptions).ConfigureAwait(false));

        var measuredRow = payload.Items.Single(row => row.Id == measured);
        AssertEx.Equal((long)int.MaxValue + 1, measuredRow.ToolSchemaTokens);
        AssertEx.Equal(expected: 4_096, measuredRow.MaxToolSchemaTokens);

        var unmeasuredRow = payload.Items.Single(row => row.Id == unmeasured);
        AssertEx.Null(unmeasuredRow.ToolSchemaTokens);
        AssertEx.Null(unmeasuredRow.MaxToolSchemaTokens);
    }

    [Test]
    public async Task ListRunEnvelopes_ReturnsTheDispatchLabels_AndNullWhenTheTurnWasNotAuto()
    {
        var conversationId = Guid.NewGuid();
        var dispatched = await SeedDispatchEnvelopeAsync(conversationId, dispatchedTier: "fast", authoredEffort: "auto").ConfigureAwait(false);
        var ordinary = await SeedDispatchEnvelopeAsync(conversationId, dispatchedTier: null, authoredEffort: null).ConfigureAwait(false);

        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/agents/run-envelopes?conversationId={conversationId}");
        Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var payload = AssertEx.NotNull(await JsonSerializer.DeserializeAsync<ListRunEnvelopesResponse>(stream, JsonOptions).ConfigureAwait(false));

        var dispatchedRow = payload.Items.Single(row => row.Id == dispatched);
        AssertEx.Equal("fast", dispatchedRow.DispatchedTier);
        AssertEx.Equal("auto", dispatchedRow.AuthoredEffort);

        var ordinaryRow = payload.Items.Single(row => row.Id == ordinary);
        AssertEx.Null(ordinaryRow.DispatchedTier);
        AssertEx.Null(ordinaryRow.AuthoredEffort);
    }

    [Test]
    public async Task ListRunEnvelopes_ReturnsTheModelReadinessDuration_AndNullWhenNothingWarmed()
    {
        // Without this on the wire, a reader cannot separate the inference-server launch and model load from the turn
        // itself, and a cold arm reads as 7x slower than a warm one for reasons that have nothing to do with the agent.
        var conversationId = Guid.NewGuid();
        var cold = await SeedReadinessEnvelopeAsync(conversationId, modelReadinessMs: 178_576L).ConfigureAwait(false);
        var warm = await SeedReadinessEnvelopeAsync(conversationId, modelReadinessMs: null).ConfigureAwait(false);

        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/agents/run-envelopes?conversationId={conversationId}");
        Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var payload = AssertEx.NotNull(await JsonSerializer.DeserializeAsync<ListRunEnvelopesResponse>(stream, JsonOptions).ConfigureAwait(false));

        AssertEx.Equal(expected: 178_576L, payload.Items.Single(row => row.Id == cold).ModelReadinessMs);
        // Null, not zero: an unmeasured turn must not read as one that proved a warm start.
        AssertEx.Null(payload.Items.Single(row => row.Id == warm).ModelReadinessMs);
    }

    private async Task<Guid> SeedReadinessEnvelopeAsync(Guid conversationId, long? modelReadinessMs)
    {
        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var id = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _ = await dbContext.Database.ExecuteSqlAsync($"""
                                                      INSERT INTO agent_execution_logs
                                                          (id, record_kind, schema_version, agent_definition_id, conversation_id, message_id, invocation_id,
                                                           model_name, provider, config_hash, terminal_status, latency_ms, success, created_at_utc,
                                                           model_readiness_ms)
                                                      VALUES ({id}, {(int)AgentExecutionLogRecordKind.ChatRunEnvelope}, {AgentRunEnvelope.CurrentSchemaVersion},
                                                              {Guid.NewGuid()}, {conversationId}, {Guid.NewGuid()}, {Guid.NewGuid()},
                                                              'llama-3.1', 'local', '', 'completed', 1500, 1, {createdAtUtc},
                                                              {modelReadinessMs});
                                                      """).ConfigureAwait(false);

        return id;
    }

    private async Task<Guid> SeedDispatchEnvelopeAsync(Guid conversationId, string? dispatchedTier, string? authoredEffort)
    {
        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var id = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _ = await dbContext.Database.ExecuteSqlAsync($"""
                                                      INSERT INTO agent_execution_logs
                                                          (id, record_kind, schema_version, agent_definition_id, conversation_id, message_id, invocation_id,
                                                           model_name, provider, config_hash, terminal_status, latency_ms, success, created_at_utc,
                                                           dispatched_tier, authored_effort)
                                                      VALUES ({id}, {(int)AgentExecutionLogRecordKind.ChatRunEnvelope}, {AgentRunEnvelope.CurrentSchemaVersion},
                                                              {Guid.NewGuid()}, {conversationId}, {Guid.NewGuid()}, {Guid.NewGuid()},
                                                              'llama-3.1', 'local', '', 'completed', 1500, 1, {createdAtUtc},
                                                              {dispatchedTier}, {authoredEffort});
                                                      """).ConfigureAwait(false);

        return id;
    }

    // Seeded through raw parameterized SQL rather than the DbSet: the entity type is internal to the persistence
    // assembly, and there is no store API that writes a run-envelope row (the real write is the terminalize command's
    // own statement, covered separately). Every value is a bound parameter.
    private async Task<Guid> SeedEnvelopeAsync(Guid conversationId, long? toolSchemaTokens, int? maxToolSchemaTokens)
    {
        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var id = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _ = await dbContext.Database.ExecuteSqlAsync($"""
                                                      INSERT INTO agent_execution_logs
                                                          (id, record_kind, schema_version, agent_definition_id, conversation_id, message_id, invocation_id,
                                                           model_name, provider, config_hash, terminal_status, latency_ms, success, created_at_utc,
                                                           tool_schema_tokens, max_tool_schema_tokens)
                                                      VALUES ({id}, {(int)AgentExecutionLogRecordKind.ChatRunEnvelope}, {AgentRunEnvelope.CurrentSchemaVersion},
                                                              {Guid.NewGuid()}, {conversationId}, {Guid.NewGuid()}, {Guid.NewGuid()},
                                                              'llama-3.1', 'local', '', 'completed', 1500, 1, {createdAtUtc},
                                                              {toolSchemaTokens}, {maxToolSchemaTokens});
                                                      """).ConfigureAwait(false);

        return id;
    }
}
