namespace XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three Operator-gated execution endpoints: authorization on every one, server-side filtering and paging that
///     can reach older rows, and the three codes a cancel can answer. The cancel is deliberately NOT key-scoped — an
///     operator must reach every integrator's rows.
/// </summary>
public sealed class IntegrationExecutionEndpointTests
{
    private const string ExecutionsRoute = "/api/local/v1/integrations/executions";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task List_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client, HttpMethod.Get, ExecutionsRoute);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task List_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsNonOperatorAsync(Factory, client, HttpMethod.Get, ExecutionsRoute);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client, HttpMethod.Get, $"{ExecutionsRoute}/{Guid.NewGuid()}");

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Cancel_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client, HttpMethod.Post, $"{ExecutionsRoute}/{Guid.NewGuid()}/cancel");

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenTheIdIsUnknown_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, $"{ExecutionsRoute}/{Guid.NewGuid()}");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Get_ReturnsTheAttributionColumnsTheListOmits()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "detail");

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, $"{ExecutionsRoute}/{seeded.ExecutionIds[0]}");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ExecutionDetailBody>(IntegrationEndpointPayloads.Json));
        AssertEx.Equal(seeded.ExecutionIds[0], body.Execution.Id);
        AssertEx.Equal(seeded.PrincipalId, body.PrincipalId, "The detail carries the integrator identity a list row deliberately does not.");
        AssertEx.Equal(seeded.KeyPrefix, body.KeyPrefix, "The credential prefix is attribution, and it belongs on the detail pane rather than in every table row.");
    }

    [Test]
    public async Task List_HonoursTheTriggerFilter()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "filter");

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, $"{ExecutionsRoute}?triggerId={seeded.OtherTriggerId}");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ExecutionListBody>(IntegrationEndpointPayloads.Json));
        AssertEx.Contains(body.Items, item => item.Id == seeded.OtherTriggerExecutionId);
        AssertEx.False(body.Items.Any(item => seeded.ExecutionIds.Contains(item.Id)), "A trigger filter must exclude every other trigger's rows.");
    }

    [Test]
    public async Task List_PagesServerSideSoOlderRowsAreReachable()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "paging");

        using var first = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{ExecutionsRoute}?triggerId={seeded.TriggerId}&limit=1&offset=0");
        using var second = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{ExecutionsRoute}?triggerId={seeded.TriggerId}&limit=1&offset=1");

        var page1 = AssertEx.NotNull(await first.Content.ReadFromJsonAsync<ExecutionListBody>(IntegrationEndpointPayloads.Json));
        var page2 = AssertEx.NotNull(await second.Content.ReadFromJsonAsync<ExecutionListBody>(IntegrationEndpointPayloads.Json));

        AssertEx.Equal(expected: 1, page1.Items.Count);
        AssertEx.Equal(expected: 1, page2.Items.Count);
        AssertEx.NotEqual(page1.Items[0].Id, page2.Items[0].Id, "Page two must return a row page one did not, or history beyond the first page is unreachable.");
    }

    [Test]
    [Arguments("limit=0")]
    [Arguments("limit=201")]
    [Arguments("offset=-1")]
    [Arguments("status=NotAStatus")]
    public async Task List_RejectsAnOutOfRangeQuery(string query)
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, $"{ExecutionsRoute}?{query}");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"'{query}' must be answered, not clamped silently.");
    }

    [Test]
    public async Task Cancel_WhenTheRowIsNonTerminal_Returns202AndStampsTheMarker()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "cancel");

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            $"{ExecutionsRoute}/{seeded.ExecutionIds[0]}/cancel");

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var row = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>().GetByIdAsync(seeded.ExecutionIds[0]));
        AssertEx.True(row.StopRequestedAtUtc is not null, "The stop marker is durable so a restart cannot resurrect the run.");
    }

    [Test]
    public async Task Cancel_WhenTheRowIsAlreadyTerminal_Returns409()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "terminal");

        using var first = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            $"{ExecutionsRoute}/{seeded.ExecutionIds[0]}/cancel");
        AssertEx.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // The first cancel terminalized a row that had not started, so the second finds it finished.
        using var second = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            $"{ExecutionsRoute}/{seeded.ExecutionIds[0]}/cancel");

        AssertEx.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Test]
    public async Task Cancel_WhenTheIdIsUnknown_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Post, $"{ExecutionsRoute}/{Guid.NewGuid()}/cancel");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    ///     The three codes the three tests above prove at runtime must also be the three the SPEC declares: the
    ///     generated client is built from this metadata, so an endpoint that answers 202/404/409 while declaring the
    ///     framework's default 204 ships a client that cannot branch on the conflict and expects a code that never
    ///     arrives.
    /// </summary>
    [Test]
    public void Cancel_DeclaresTheThreeCodesItAnswers_AndNoDefault204()
    {
        var declared = Factory.Services.GetRequiredService<EndpointDataSource>()
                              .Endpoints
                              .OfType<RouteEndpoint>()
                              .Where(static endpoint => endpoint.RoutePattern.RawText?.EndsWith("integrations/executions/{executionId}/cancel", StringComparison.Ordinal) == true)
                              .SelectMany(static endpoint => endpoint.Metadata.OfType<IProducesResponseTypeMetadata>())
                              .Select(static metadata => metadata.StatusCode)
                              .ToHashSet();

        AssertEx.NotEmpty(declared, "The operator cancel route must be mapped, or there is no contract to assert.");
        AssertEx.Contains(declared, StatusCodes.Status202Accepted, "Cancellation is REQUESTED, so the success code is 202.");
        AssertEx.Contains(declared, StatusCodes.Status404NotFound, "An unknown execution id is a 404 and the client has to know it.");
        AssertEx.Contains(declared, StatusCodes.Status409Conflict, "An already-terminal row is a 409, and a generated client that does not declare it cannot type its error branch.");
        AssertEx.False(declared.Contains(StatusCodes.Status204NoContent), "This endpoint never sends 204; the framework's default has to be cleared or the spec promises a code that never arrives.");
    }

    private async Task<Seeded> SeedAsync(HttpClient client, string prefix)
    {
        // A REAL credential: the admission transaction re-reads the key row for revocation, so a fabricated prefix is
        // refused exactly as a revoked one would be.
        var key = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, $"{prefix}-key");

        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
        var principalId = key.View.PrincipalId;
        var keyPrefix = key.View.KeyPrefix;
        var triggerId = Guid.NewGuid();
        var otherTriggerId = Guid.NewGuid();

        var first = await AdmitAsync(store, triggerId, principalId, keyPrefix, receivedAtUtc: 1_000);
        var second = await AdmitAsync(store, triggerId, principalId, keyPrefix, receivedAtUtc: 2_000);
        var other = await AdmitAsync(store, otherTriggerId, principalId, keyPrefix, receivedAtUtc: 3_000);
        return new Seeded(principalId, keyPrefix, triggerId, otherTriggerId, [first, second], other);
    }

    /// <summary>
    ///     Writes an admitted row through the real store. Nothing reaches the coordinator's channel this way, so the
    ///     suite asserts on stable rows instead of racing a background run that has no model to reach.
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
                new byte[] { 7 },
                keyPrefix,
                receivedAtUtc,
                new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, IntegrationStreamEventTypes.ExecutionAccepted, DetailJson: null, receivedAtUtc)),
            maxActive: 4096,
            maxActivePerPrincipal: 4096);
        AssertEx.True(admitted, "Seeding the execution row must be admitted.");
        return executionId;
    }

    private sealed record Seeded(Guid PrincipalId,
        string KeyPrefix,
        Guid TriggerId,
        Guid OtherTriggerId,
        IReadOnlyList<Guid> ExecutionIds,
        Guid OtherTriggerExecutionId);

    private sealed record ExecutionSummaryBody(Guid Id,
        Guid TriggerId,
        Guid SessionId,
        string Status,
        long ReceivedAtUtc,
        long? StartedAtUtc,
        long? EndedAtUtc,
        string? FailureCategory,
        string? FailureSummary,
        int OutputCount);

    private sealed record ExecutionListBody(IReadOnlyList<ExecutionSummaryBody> Items);

    private sealed record ExecutionDetailBody(ExecutionSummaryBody Execution,
        Guid PrincipalId,
        string KeyPrefix,
        Guid RequestId,
        Guid InvocationId,
        long OutputBytes,
        long LastSequence,
        long Version,
        long? StopRequestedAtUtc);
}
