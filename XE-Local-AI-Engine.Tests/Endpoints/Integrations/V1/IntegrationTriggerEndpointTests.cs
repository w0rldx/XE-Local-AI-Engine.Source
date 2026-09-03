namespace XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The five Operator-gated trigger endpoints: authorization on every one, the create's 201 with a resolvable
///     <c>Location</c>, and the validation and conflict answers a UI has to render.
/// </summary>
public sealed class IntegrationTriggerEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task List_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client, HttpMethod.Get, IntegrationEndpointPayloads.TriggersRoute)
                                                              .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task List_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsNonOperatorAsync(Factory, client, HttpMethod.Get, IntegrationEndpointPayloads.TriggersRoute)
                                                              .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.TriggersRoute,
            IntegrationEndpointPayloads.TriggerBody("anon-probe", Guid.NewGuid())).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsNonOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.TriggersRoute,
            IntegrationEndpointPayloads.TriggerBody("viewer-probe", Guid.NewGuid())).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenValid_Returns201WithAResolvableLocation()
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "trigger-create-agent").ConfigureAwait(false);

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.TriggersRoute,
            IntegrationEndpointPayloads.TriggerBody("create-probe", agentId)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        var view = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<IntegrationTriggerBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.NotEqual(Guid.Empty, view.Id);
        AssertEx.Equal("create-probe", view.Name);
        AssertEx.True(view.AcceptedInputKinds.SequenceEqual(new[]
            {
                "text",
                "json"
            }),
            "The [Flags] enum crosses the wire as member names, not as an integer sum a generated SDK cannot read.");

        var location = AssertEx.NotNull(response.Headers.Location);
        using var followUp = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, location.ToString()).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, followUp.StatusCode, "The Location the create returns must resolve through the get endpoint.");
    }

    [Test]
    [Arguments("Sensor-Feed")]
    [Arguments("a")]
    [Arguments("-leading-hyphen")]
    [Arguments("has spaces")]
    public async Task Create_WithAnInvalidName_Returns400(string name)
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, $"name-probe-{Guid.NewGuid():N}").ConfigureAwait(false);

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.TriggersRoute,
            IntegrationEndpointPayloads.TriggerBody(name, agentId)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Create_WithNoAcceptedInputKinds_Returns400()
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "kinds-probe").ConfigureAwait(false);

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.TriggersRoute,
            IntegrationEndpointPayloads.TriggerBody("kinds-probe", agentId, acceptedInputKinds: [])).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Create_WithAnUnknownInputKind_Returns400()
    {
        // Silently dropping the unknown member would save a trigger accepting LESS than the operator asked for.
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "unknown-kind-probe").ConfigureAwait(false);

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.TriggersRoute,
            IntegrationEndpointPayloads.TriggerBody("unknown-kind-probe", agentId, acceptedInputKinds: ["text", "binary"])).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Create_WithAMissingAgent_Returns400()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.TriggersRoute,
            IntegrationEndpointPayloads.TriggerBody("missing-agent-probe", Guid.NewGuid())).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Create_WithADuplicateName_Returns409()
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "duplicate-probe-agent").ConfigureAwait(false);
        _ = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, "duplicate-probe", agentId).ConfigureAwait(false);

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.TriggersRoute,
            IntegrationEndpointPayloads.TriggerBody("duplicate-probe", agentId)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Test]
    public async Task Get_WithAnUnknownId_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{IntegrationEndpointPayloads.TriggersRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task List_ReturnsTheCreatedTrigger()
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "list-probe-agent").ConfigureAwait(false);
        var created = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, "list-probe", agentId).ConfigureAwait(false);

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, IntegrationEndpointPayloads.TriggersRoute)
                                                              .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<IntegrationTriggerListBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.Contains(body.Items, item => item.Id == created.Id);
    }

    [Test]
    public async Task Update_AppliesTheEditAndRejectsAStaleVersionWith409()
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "update-probe-agent").ConfigureAwait(false);
        var created = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, "update-probe", agentId).ConfigureAwait(false);
        var route = $"{IntegrationEndpointPayloads.TriggersRoute}/{created.Id}";

        using var updated = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Put,
            route,
            IntegrationEndpointPayloads.UpdateBody(agentId, created.Version, displayName: "Renamed", acceptedInputKinds: ["text"])).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, updated.StatusCode);
        var view = AssertEx.NotNull(await updated.Content.ReadFromJsonAsync<IntegrationTriggerBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.Equal("Renamed", view.DisplayName);
        AssertEx.Equal("update-probe", view.Name, "The external name is not editable through the update.");
        AssertEx.Equal(created.Version + 1, view.Version);

        using var stale = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Put,
            route,
            IntegrationEndpointPayloads.UpdateBody(agentId, created.Version, displayName: "Renamed again")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Test]
    public async Task Update_WithAnUnknownId_Returns404()
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "update-404-agent").ConfigureAwait(false);

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Put,
            $"{IntegrationEndpointPayloads.TriggersRoute}/{Guid.NewGuid()}",
            IntegrationEndpointPayloads.UpdateBody(agentId, expectedVersion: 1)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Delete_Returns204ThenNotFound()
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "delete-probe-agent").ConfigureAwait(false);
        var created = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, "delete-probe", agentId).ConfigureAwait(false);
        var route = $"{IntegrationEndpointPayloads.TriggersRoute}/{created.Id}";

        using var deleted = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Delete, route).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var again = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Delete, route).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Test]
    public async Task Delete_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsNonOperatorAsync(Factory,
            client,
            HttpMethod.Delete,
            $"{IntegrationEndpointPayloads.TriggersRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
