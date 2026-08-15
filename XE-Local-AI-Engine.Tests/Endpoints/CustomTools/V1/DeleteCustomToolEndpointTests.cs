namespace XE_Local_AI_Engine.Tests.Endpoints.CustomTools.V1;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>DELETE custom-tools/{customToolId}</c>: operator-gated, 204 on the first delete and 404 on the second — the
///     delete is not idempotent-by-204, so a UI that retries learns the row is already gone.
/// </summary>
public sealed class DeleteCustomToolEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Delete_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAnonymousAsync(client,
            HttpMethod.Delete,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsNonOperatorAsync(Factory,
            client,
            HttpMethod.Delete,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenUnknownId_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Delete,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenExisting_Returns204ThenGetReturns404()
    {
        using var client = Factory.CreateClient();
        var toolId = await CustomToolEndpointPayloads.CreateAsync(Factory, client, "delete_probe").ConfigureAwait(false);

        using var deleted = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Delete,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{toolId}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var reread = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"{CustomToolEndpointPayloads.DefinitionsRoute}/{toolId}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, reread.StatusCode);
    }
}
