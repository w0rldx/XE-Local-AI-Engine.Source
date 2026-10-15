namespace XE_Local_AI_Engine.Tests.Endpoints.CustomTools.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>POST custom-tools</c>: operator-gated, 201 + a resolvable Location on success, and 400 when the server-side
///     danger acknowledgement is missing — a control that must never be enforceable from the client alone.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class CreateCustomToolEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Create_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAnonymousAsync(client,
            HttpMethod.Post,
            CustomToolEndpointPayloads.DefinitionsRoute,
            CustomToolEndpointPayloads.HttpFetchDefinition("create_anon"));

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsNonOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            CustomToolEndpointPayloads.DefinitionsRoute,
            CustomToolEndpointPayloads.HttpFetchDefinition("create_viewer"));

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenAcknowledgementMissing_Returns400()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            CustomToolEndpointPayloads.DefinitionsRoute,
            CustomToolEndpointPayloads.HttpFetchDefinition("create_unacked", acknowledged: false));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenValid_Returns201WithViewAndResolvableLocation()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            CustomToolEndpointPayloads.DefinitionsRoute,
            CustomToolEndpointPayloads.HttpFetchDefinition("create_probe"));

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);

        var view = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json));
        AssertEx.NotEqual(Guid.Empty, view.Id);
        AssertEx.Equal(CustomToolKind.HttpFetch, view.Kind);
        AssertEx.Equal(CustomToolMode.Fixed, view.Mode);
        AssertEx.True(view.Acknowledged, "The stored view must echo the acknowledgement the create carried.");
        AssertEx.NotNull(view.Http);
        AssertEx.Null(view.Command);

        // Send.CreatedAtAsync<GetCustomToolEndpoint> resolves the Location through the global NameGenerator; a GET on
        // it must land on the same tool.
        var location = AssertEx.NotNull(response.Headers.Location);
        using var followUp = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            location.ToString());

        AssertEx.Equal(HttpStatusCode.OK, followUp.StatusCode);
        var fetched = AssertEx.NotNull(await followUp.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json));
        AssertEx.Equal(view.Id, fetched.Id);
    }

    [Test]
    public async Task Create_WithAllowUnattendedAndPrivateHost_RoundTripsBothFields()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            CustomToolEndpointPayloads.DefinitionsRoute,
            CustomToolEndpointPayloads.HttpFetchDefinition("create_lan_action",
                urlTemplate: "http://192.168.1.20:8123/api/states",
                allowUnattended: true,
                privateHosts: ["192.168.1.20"]));

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        var view = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json));

        using var followUp = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{view.Id}");
        var fetched = AssertEx.NotNull(await followUp.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json));
        AssertEx.True(fetched.AllowUnattended, "The stored flag must read back.");
        AssertEx.Equal("192.168.1.20", string.Join(",", AssertEx.NotNull(fetched.Http).PrivateHosts));
    }

    [Test]
    public async Task Create_DetachedCommand_RoundTripsTheFlag()
    {
        using var client = Factory.CreateClient();
        var payload = new
        {
            name = "create_detached_launch",
            description = "Endpoint suite fixture tool.",
            kind = "Command",
            mode = "Fixed",
            enabled = true,
            acknowledged = true,
            parameters = Array.Empty<object>(),
            command = new
            {
                executable = "/opt/example/bin/word-processor",
                argsTemplate = Array.Empty<string>(),
                timeoutSeconds = 0,
                env = Array.Empty<object>(),
                detach = true
            }
        };

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            CustomToolEndpointPayloads.DefinitionsRoute,
            payload);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        var view = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json));
        using var followUp = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{view.Id}");
        var fetched = AssertEx.NotNull(await followUp.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json));
        AssertEx.True(AssertEx.NotNull(fetched.Command).Detach, "The stored detach flag must read back.");
    }

    [Test]
    public async Task Create_WithUnlistedPrivateLiteral_Returns400()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            CustomToolEndpointPayloads.DefinitionsRoute,
            CustomToolEndpointPayloads.HttpFetchDefinition("create_lan_unlisted", urlTemplate: "http://192.168.1.20:8123/api/states"));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
