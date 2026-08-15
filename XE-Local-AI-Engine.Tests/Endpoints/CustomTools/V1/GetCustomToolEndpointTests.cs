namespace XE_Local_AI_Engine.Tests.Endpoints.CustomTools.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>GET custom-tools/{customToolId}</c>: operator-gated, 404 for an unknown id, and — the load-bearing one — a
///     secret header value is returned MASKED. The read path must never hand a stored secret back, not even to the
///     operator on the loopback surface.
/// </summary>
public sealed class GetCustomToolEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Get_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAnonymousAsync(client,
            HttpMethod.Get,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsNonOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenUnknownId_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenSecretHeaderStored_ReturnsSentinelNeverCleartext()
    {
        const string Cleartext = "super-secret-api-key-value";
        using var client = Factory.CreateClient();
        var toolId = await CustomToolEndpointPayloads.CreateAsync(Factory, client, "get_secret_probe", Cleartext).ConfigureAwait(false);

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{toolId}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.False(raw.Contains(Cleartext, StringComparison.Ordinal),
            "The custom-tool read path must never return a stored secret in cleartext.");

        var view = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.Equal(toolId, view.Id);
        var header = AssertEx.NotNull(view.Http).Headers.Single(candidate => candidate.Name == "X-Api-Key");
        AssertEx.True(header.IsSecret, "The header must stay marked secret across the round trip.");
        AssertEx.Equal(CustomToolEndpointPayloads.SecretSentinel, header.Value);
    }
}
