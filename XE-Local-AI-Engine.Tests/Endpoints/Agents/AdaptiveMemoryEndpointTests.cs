namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Adaptive agent memory endpoint contract tests:
///     <list type="bullet">
///         <item>the playbook list endpoint surfaces <c>memoryScope</c>/<c>source</c> and filters on <c>?scope=</c>;</item>
///         <item>governance still gates an <c>Extracted</c>/<c>Suggested</c> candidate (no Enabled without eval-gate + approval);</item>
///         <item>the execution-logs diagnostics endpoint returns paged metadata, Operator-gated, with no message content;</item>
///         <item>the agent create/update contract carries <c>defaultTemporaryChat</c> round-trip.</item>
///     </list>
/// </summary>
public sealed class AdaptiveMemoryEndpointTests
{
    private static string PlaybookRoute(Guid agentDefinitionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook";
    }

    private static string PromoteRoute(Guid agentDefinitionId, Guid actionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook/{actionId}/promote";
    }

    private static string ExecutionLogsRoute(Guid agentDefinitionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/execution-logs";
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Governance reuse — an Extracted/Suggested candidate cannot reach Enabled without the eval gate + approval.
    // ----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExtractedCandidate_CannotReachEnabled_WithoutEvalGateAndApproval()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);
        // Seed a candidate exactly as the extraction service would: Suggested + Source=Extracted + a typed scope.
        var actionId = await SeedExtractedSuggestionAsync(factory, agentId, MemoryScope.Procedural).ConfigureAwait(false);

        // Attempt to promote with no recorded eval → the SAME eval gate that governs Manual/Analysis blocks it (409).
        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(agentId, actionId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.Equal("EvalRequired", document.RootElement.GetProperty("status").GetString());

        // The blocked promote leaves the Extracted candidate Suggested (still inert) — governance was not bypassed.
        using var verifyScope = factory.Services.CreateScope();
        var service = verifyScope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var stored = AssertEx.NotNull(await service.GetByIdAsync(actionId).ConfigureAwait(false));
        AssertEx.Equal(PlaybookActionState.Suggested, stored.State);
        AssertEx.Equal(PlaybookActionSource.Extracted, stored.Source);
    }

    [Test]
    public async Task ExtractedCandidate_AfterEvalGateAndApproval_ReachesEnabled()
    {
        // The positive half: once the eval gate passes (and the cap is not reached) the SAME promote route enables the
        // Extracted candidate — proving governance reuse works end-to-end for the new Source, not just that it blocks.
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);
        var actionId = await SeedExtractedSuggestionAsync(factory, agentId, MemoryScope.Procedural).ConfigureAwait(false);
        await RecordPassingEvalAsync(factory, agentId, actionId).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(agentId, actionId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var service = verifyScope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var stored = AssertEx.NotNull(await service.GetByIdAsync(actionId).ConfigureAwait(false));
        AssertEx.Equal(PlaybookActionState.Enabled, stored.State);
        AssertEx.Equal(PlaybookActionSource.Extracted, stored.Source);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Playbook list — scope filter + provenance/scope surfacing.
    // ----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ListPlaybook_WhenScopeSupplied_FiltersToThatScope()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);
        var proceduralId = await SeedExtractedSuggestionAsync(factory, agentId, MemoryScope.Procedural).ConfigureAwait(false);
        _ = await SeedExtractedSuggestionAsync(factory, agentId, MemoryScope.Failure).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PlaybookRoute(agentId)}?scope=Procedural");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");

        AssertEx.Equal(expected: 1, items.GetArrayLength());
        var item = items[0];
        AssertEx.Equal(proceduralId.ToString(), item.GetProperty("id").GetString());
        AssertEx.Equal("Procedural", item.GetProperty("memoryScope").GetString());
        AssertEx.Equal("Extracted", item.GetProperty("source").GetString());
    }

    [Test]
    public async Task ListPlaybook_WhenNoScope_ReturnsAllActions()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);
        _ = await SeedExtractedSuggestionAsync(factory, agentId, MemoryScope.Procedural).ConfigureAwait(false);
        _ = await SeedExtractedSuggestionAsync(factory, agentId, MemoryScope.Failure).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, PlaybookRoute(agentId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.Equal(expected: 2, document.RootElement.GetProperty("items").GetArrayLength());
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Execution-logs diagnostics endpoint.
    // ----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecutionLogs_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ExecutionLogsRoute(Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ExecutionLogs_ReturnsPagedMetadata_NoContent()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);
        await SeedExecutionLogAsync(factory, agentId, success: true, errorClass: null).ConfigureAwait(false);
        await SeedExecutionLogAsync(factory, agentId, success: false, "InvalidOperationException").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, ExecutionLogsRoute(agentId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");
        AssertEx.Equal(expected: 2, items.GetArrayLength());

        foreach (var item in items.EnumerateArray())
        {
            // Metadata-only contract: the projection exposes telemetry fields and an exception TYPE name, never any
            // message/transcript content. A raw scan asserts no content-bearing property leaked into the wire shape.
            AssertEx.True(item.TryGetProperty("configHash", out _), "Execution log row should carry the config hash metadata.");
            AssertEx.True(item.TryGetProperty("latencyMs", out _), "Execution log row should carry latency metadata.");
            AssertEx.False(item.TryGetProperty("content", out _), "Execution log must not carry message content.");
            AssertEx.False(item.TryGetProperty("errorMessage", out _), "Execution log must not carry an exception message.");
        }

        var failed = items.EnumerateArray().First(static element => !element.GetProperty("success").GetBoolean());
        AssertEx.Equal("InvalidOperationException", failed.GetProperty("errorClass").GetString());
    }

    [Test]
    public async Task ExecutionLogs_WhenAgentMissing_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ExecutionLogsRoute(Guid.NewGuid()));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Agent contract — defaultTemporaryChat round-trip on create + update.
    // ----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateAgent_CarriesDefaultTemporaryChat_RoundTrip()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/agents")
        {
            Content = JsonContent.Create(new
            {
                name = "Temp-default Agent",
                instructions = "You are a careful engineering agent.",
                defaultTemporaryChat = true
            })
        };
        factory.AddNodeBearerToken(createRequest);
        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var createPayload = await createResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var createDocument = JsonDocument.Parse(createPayload);
        AssertEx.True(createDocument.RootElement.GetProperty("defaultTemporaryChat").GetBoolean(), "Create response should echo defaultTemporaryChat=true.");

        // GET round-trip proves the flag persisted (not just echoed on the create response).
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, createResponse.Headers.Location);
        factory.AddNodeBearerToken(getRequest);
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = await getResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var getDocument = JsonDocument.Parse(getPayload);
        AssertEx.True(getDocument.RootElement.GetProperty("defaultTemporaryChat").GetBoolean(), "Persisted defaultTemporaryChat should round-trip on GET.");
    }

    [Test]
    public async Task UpdateAgent_CarriesDefaultTemporaryChat_RoundTrip()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/local/v1/agents/{agentId}")
        {
            Content = JsonContent.Create(new
            {
                name = "Updated Agent",
                instructions = "You are a careful engineering agent.",
                defaultTemporaryChat = true
            })
        };
        factory.AddNodeBearerToken(updateRequest);
        using var updateResponse = await client.SendAsync(updateRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var payload = await updateResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.True(document.RootElement.GetProperty("defaultTemporaryChat").GetBoolean(), "Update response should reflect defaultTemporaryChat=true.");

        // Confirm via the store that the flag persisted.
        using var verifyScope = factory.Services.CreateScope();
        var store = verifyScope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var stored = AssertEx.NotNull(await store.GetByIdAsync(agentId).ConfigureAwait(false));
        AssertEx.True(stored.DefaultTemporaryChat, "Persisted definition should carry DefaultTemporaryChat=true after update.");
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Seed helpers.
    // ----------------------------------------------------------------------------------------------------------------

    private static async Task<Guid> SeedAgentAsync(TestingWebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var agent = await store.AddAsync(new AgentDefinitionInput("Owner",
            Description: null,
            "You are a careful engineering agent.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null)).ConfigureAwait(false);
        return agent.Id;
    }

    private static async Task<Guid> SeedExtractedSuggestionAsync(TestingWebAppFactory factory, Guid agentDefinitionId, MemoryScope scope)
    {
        // Mirrors the extraction service write: Suggested + Source=Extracted + a typed MemoryScope, with evidence ids.
        using var scopeProvider = factory.Services.CreateScope();
        var store = scopeProvider.ServiceProvider.GetRequiredService<IPlaybookActionStore>();
        var created = await store.AddAsync(new PlaybookActionInput(agentDefinitionId,
            PlaybookActionState.Suggested,
            PlaybookActionSource.Extracted,
            TriggerCondition: null,
            $"Lesson learned for {scope}.",
            scope.ToString(),
            Priority: 100,
            [Guid.NewGuid()],
            Confidence: 0.8d,
            MemoryScope: scope)).ConfigureAwait(false);
        return created.Id;
    }

    private static async Task SeedExecutionLogAsync(TestingWebAppFactory factory, Guid agentDefinitionId, bool success, string? errorClass)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
        _ = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-model",
            "deadbeef",
            LatencyMs: 1_234,
            success,
            PromptTokens: 10,
            CompletionTokens: 20,
            errorClass)).ConfigureAwait(false);
    }

    private static async Task RecordPassingEvalAsync(TestingWebAppFactory factory, Guid agentDefinitionId, Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var current = AssertEx.NotNull(await service.GetByIdAsync(actionId).ConfigureAwait(false));

        var eval = new PlaybookEvalResult(Passed: true,
            EvaluatedAtUtc: 1_000,
            current.Version,
            "test-model",
            GoldenCaseCount: 1,
            GoldenCaseTotal: 1,
            BaselinePassCount: 1,
            CandidatePassCount: 1,
            RegressedCaseCount: 0,
            ImprovedCaseCount: 0,
            []);
        var json = JsonSerializer.Serialize(eval, PlaybookEvalResult.SerializerOptions);
        _ = AssertEx.NotNull(await service.RecordEvalResultAsync(agentDefinitionId, actionId, json).ConfigureAwait(false));
    }
}
