namespace XE_Local_AI_Engine.Tests.Endpoints.CustomTools.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>GET custom-tools</c>: operator-gated, and wraps the views in a named <c>items</c> envelope so the generated
///     client has a stable list schema.
/// </summary>
public sealed class ListCustomToolsEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task List_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAnonymousAsync(client,
            HttpMethod.Get,
            CustomToolEndpointPayloads.DefinitionsRoute).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task List_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsNonOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            CustomToolEndpointPayloads.DefinitionsRoute).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task List_WhenOperator_Returns200WithItemsEnvelope()
    {
        using var client = Factory.CreateClient();
        var toolId = await CustomToolEndpointPayloads.CreateAsync(Factory, client, "list_probe").ConfigureAwait(false);

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            CustomToolEndpointPayloads.DefinitionsRoute).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ListCustomToolsResponse>(CustomToolEndpointPayloads.Json).ConfigureAwait(false);
        var items = AssertEx.NotNull(payload).Items;
        AssertEx.ContainsSingle(items, view => view.Id == toolId);

        // The service normalizes an authored slug to the reserved MAF tool name, and the list view must expose that
        // stored name — the model routes against it.
        AssertEx.Equal("custom__list_probe", items.Single(view => view.Id == toolId).Name);
    }
}
