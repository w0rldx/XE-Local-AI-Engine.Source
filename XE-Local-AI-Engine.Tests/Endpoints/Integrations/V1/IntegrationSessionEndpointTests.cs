namespace XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three Operator-gated session endpoints. They are deliberately NOT principal-scoped — an operator reading
///     their own node is not acting as an integrator — so an unknown id is a plain 404 rather than the external
///     family's masked one, and every row is reachable.
/// </summary>
public sealed class IntegrationSessionEndpointTests
{
    private const string SessionsRoute = "/api/local/v1/integrations/sessions";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task List_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client, HttpMethod.Get, SessionsRoute);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task List_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsNonOperatorAsync(Factory, client, HttpMethod.Get, SessionsRoute);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client, HttpMethod.Get, $"{SessionsRoute}/{Guid.NewGuid()}");

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client, HttpMethod.Delete, $"{SessionsRoute}/{Guid.NewGuid()}");

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenTheIdIsUnknown_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, $"{SessionsRoute}/{Guid.NewGuid()}");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Get_CarriesTheTriggerNameAndTheCountersTheUiRenders()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "detail");

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, $"{SessionsRoute}/{seeded.SessionIds[0]}");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<SessionBody>(IntegrationEndpointPayloads.Json));
        AssertEx.Equal(seeded.SessionIds[0], body.Id);
        AssertEx.Equal(seeded.TriggerName, body.TriggerName, "The trigger NAME is what an integrator addresses, so the row carries it rather than an id alone.");
        AssertEx.Equal(seeded.PrincipalId, body.PrincipalId, "The operator surface names the OWNING integrator; without it the admin UI cannot say whose session a row is.");
        AssertEx.Equal(expected: 1, body.ExecutionCount);
        AssertEx.Equal("Active", body.Status);
    }

    [Test]
    public async Task List_HonoursTheTriggerFilterAndPagesServerSide()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "filter");

        using var filtered = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, $"{SessionsRoute}?triggerId={seeded.TriggerId}");
        using var first = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{SessionsRoute}?triggerId={seeded.TriggerId}&limit=1&offset=0");
        using var second = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{SessionsRoute}?triggerId={seeded.TriggerId}&limit=1&offset=1");

        var all = AssertEx.NotNull(await filtered.Content.ReadFromJsonAsync<SessionListBody>(IntegrationEndpointPayloads.Json));
        AssertEx.Equal(expected: 2, all.Items.Count);
        AssertEx.True(all.Items.All(item => item.TriggerId == seeded.TriggerId), "A trigger filter must exclude every other trigger's rows.");

        var page1 = AssertEx.NotNull(await first.Content.ReadFromJsonAsync<SessionListBody>(IntegrationEndpointPayloads.Json));
        var page2 = AssertEx.NotNull(await second.Content.ReadFromJsonAsync<SessionListBody>(IntegrationEndpointPayloads.Json));
        AssertEx.NotEqual(page1.Items[0].Id, page2.Items[0].Id, "Page two must return a row page one did not, or older sessions are unreachable.");

        // The pager's total is the whole filtered set: a one-row window over the trigger's two sessions still reports
        // two, which is what lets the UI page instead of showing a bounded-window note.
        AssertEx.Equal(expected: 2, all.TotalCount);
        AssertEx.Equal(expected: 2, page1.TotalCount, "The total must ignore limit and offset.");
    }

    [Test]
    [Arguments("limit=0")]
    [Arguments("limit=201")]
    [Arguments("offset=-1")]
    [Arguments("status=NotAStatus")]
    public async Task List_RejectsAnOutOfRangeQuery(string query)
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, $"{SessionsRoute}?{query}");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"'{query}' must be answered, not clamped silently.");
    }

    [Test]
    public async Task Delete_WhileAnExecutionIsStillAccepted_Returns409AndKeepsTheRow()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "busy");

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Delete, $"{SessionsRoute}/{seeded.SessionIds[0]}");

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        _ = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<IIntegrationSessionStore>().GetByIdAsync(seeded.SessionIds[0]),
            "A refused delete leaves the session and its run intact.");
    }

    [Test]
    public async Task Delete_WhenTheIdIsUnknown_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Delete, $"{SessionsRoute}/{Guid.NewGuid()}");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<Seeded> SeedAsync(HttpClient client, string prefix)
    {
        // A REAL credential: the admission transaction re-reads the key row for revocation, so a fabricated prefix is
        // refused exactly as a revoked one would be.
        var key = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, $"{prefix}-key");
        var triggerName = $"session-{prefix}";

        using var scope = Factory.Services.CreateScope();

        // The trigger goes in through the STORE rather than the admin endpoint: the endpoint probes the target agent
        // definition, and these suites assert the session surface rather than agent CRUD.
        var trigger = await scope.ServiceProvider.GetRequiredService<IIntegrationTriggerStore>()
                                 .CreateAsync(new IntegrationTriggerCreateCommand(Guid.NewGuid(),
                                     triggerName,
                                     "Session feed",
                                     Description: null,
                                     Enabled: true,
                                     IntegrationTargetKind.Agent,
                                     Guid.NewGuid(),
                                     IntegrationSessionPolicy.CallerManaged,
                                     IntegrationInputKinds.Text | IntegrationInputKinds.Json));

        var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
        var principalId = key.View.PrincipalId;
        var keyPrefix = key.View.KeyPrefix;

        var first = await AdmitAsync(store, trigger.Id, principalId, keyPrefix, receivedAtUtc: 1_000);
        var second = await AdmitAsync(store, trigger.Id, principalId, keyPrefix, receivedAtUtc: 2_000);
        _ = await AdmitAsync(store, Guid.NewGuid(), principalId, keyPrefix, receivedAtUtc: 3_000);
        return new Seeded(trigger.Id, triggerName, principalId, [first, second]);
    }

    /// <summary>
    ///     Writes an admitted row — and with it its session — through the real store. Nothing reaches the coordinator's
    ///     channel this way, so the suite asserts on stable rows instead of racing a run that has no model to reach.
    /// </summary>
    private static async Task<Guid> AdmitAsync(IIntegrationExecutionStore store, Guid triggerId, Guid principalId, string keyPrefix, long receivedAtUtc)
    {
        var executionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var admitted = await store.AcceptAsync(new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, triggerId, Guid.NewGuid(), Guid.NewGuid()),
                executionId,
                triggerId,
                sessionId,
                principalId,
                Guid.NewGuid(),
                new byte[]
                {
                    7
                },
                keyPrefix,
                receivedAtUtc,
                new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, IntegrationStreamEventTypes.ExecutionAccepted, DetailJson: null, receivedAtUtc)),
            maxActive: 4096,
            maxActivePerPrincipal: 4096);
        AssertEx.True(admitted, "Seeding the session row must be admitted.");
        return sessionId;
    }

    private sealed record Seeded(Guid TriggerId, string TriggerName, Guid PrincipalId, IReadOnlyList<Guid> SessionIds);

    private sealed record SessionBody(
        Guid Id,
        Guid TriggerId,
        string TriggerName,
        Guid PrincipalId,
        Guid AgentDefinitionId,
        string Status,
        long CreatedAtUtc,
        long LastActivityUtc,
        int ExecutionCount);

    private sealed record SessionListBody(IReadOnlyList<SessionBody> Items, int TotalCount);
}
