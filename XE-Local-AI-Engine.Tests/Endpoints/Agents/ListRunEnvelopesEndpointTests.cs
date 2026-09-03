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
