namespace XE_Local_AI_Engine.Tests.Endpoints.CustomTools.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>PUT custom-tools/{customToolId}</c>: operator-gated, 404 for an unknown id, 400 when the replacement fails
///     validation, and 200 on success — with the version bumped only when the model-visible config actually changed.
/// </summary>
public sealed class UpdateCustomToolEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Update_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAnonymousAsync(client,
            HttpMethod.Put,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}",
            CustomToolEndpointPayloads.HttpFetchDefinition("update_anon")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Update_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsNonOperatorAsync(Factory,
            client,
            HttpMethod.Put,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}",
            CustomToolEndpointPayloads.HttpFetchDefinition("update_viewer")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Update_WhenUnknownId_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Put,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}",
            CustomToolEndpointPayloads.HttpFetchDefinition("update_missing")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Update_WhenAcknowledgementMissing_Returns400()
    {
        using var client = Factory.CreateClient();
        var toolId = await CustomToolEndpointPayloads.CreateAsync(Factory, client, "update_unacked").ConfigureAwait(false);

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Put,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{toolId}",
            CustomToolEndpointPayloads.HttpFetchDefinition("update_unacked", acknowledged: false)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Update_WhenContentChanged_Returns200WithBumpedVersion()
    {
        using var client = Factory.CreateClient();
        var toolId = await CustomToolEndpointPayloads.CreateAsync(Factory, client, "update_probe").ConfigureAwait(false);
        var before = await ReadVersionAsync(client, toolId).ConfigureAwait(false);

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Put,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{toolId}",
            CustomToolEndpointPayloads.HttpFetchDefinition("update_probe", urlTemplate: "https://api.example.com/other")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.Equal(toolId, after.Id);
        AssertEx.Equal("https://api.example.com/other", AssertEx.NotNull(after.Http).UrlTemplate);
        AssertEx.Equal(before + 1, after.Version, "A replacement that changes the model-visible config must bump the version.");
    }

    [Test]
    public async Task Update_WhenOnlyEnabledToggled_Returns200WithoutBumpingVersion()
    {
        using var client = Factory.CreateClient();
        var toolId = await CustomToolEndpointPayloads.CreateAsync(Factory, client, "update_toggle").ConfigureAwait(false);
        var before = await ReadVersionAsync(client, toolId).ConfigureAwait(false);

        // Enabled gates membership in the offered set, not what the model sees, so toggling it alone is not a content
        // change — the version must hold. Agents pin tool versions, so a spurious bump invalidates them for nothing.
        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Put,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{toolId}",
            CustomToolEndpointPayloads.HttpFetchDefinition("update_toggle", enabled: false)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.False(after.Enabled, "The toggle itself must still be applied.");
        AssertEx.Equal(before, after.Version, "Toggling Enabled alone must not bump the version.");
    }

    private async Task<int> ReadVersionAsync(HttpClient client, Guid toolId)
    {
        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{toolId}").ConfigureAwait(false);

        var view = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<CustomToolView>(CustomToolEndpointPayloads.Json).ConfigureAwait(false));
        return view.Version;
    }
}
