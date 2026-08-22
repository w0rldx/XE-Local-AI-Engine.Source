namespace XE_Local_AI_Engine.Tests.Endpoints.Workspaces;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WorkspaceEndpointTests
{
    private const string Route = "/api/local/v1/workspaces";
    private const string McpKey = "xemcp_workspace-test";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task WorkspaceEndpoints_WithoutOperatorAuthentication_AreUnauthorized()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateClient();

        using var post = await client.PostAsJsonAsync(Route, new
        {
            alias = "repo",
            hostPath = HostPath("trusted", "repo")
        }).ConfigureAwait(false);
        using var get = await client.GetAsync(Route).ConfigureAwait(false);
        using var delete = await client.DeleteAsync($"{Route}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }

    [Test]
    public async Task WorkspaceMutations_WithMcpApiKey_AreUnauthorized()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
                alias = "repo",
                hostPath = HostPath("trusted", "repo")
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpKey);

        using var postResponse = await client.SendAsync(request).ConfigureAwait(false);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{Route}/{Guid.NewGuid()}");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpKey);
        using var deleteResponse = await client.SendAsync(deleteRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, postResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, deleteResponse.StatusCode);
    }

    [Test]
    public async Task CreateAndList_WhenOperatorAuthenticated_ExposeOnlyOpaqueReadOnlyMetadata()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateClient();
        var secretPath = HostPath("secret", "operator", Guid.NewGuid().ToString("N"));

        using var createRequest = OperatorRequest(factory, HttpMethod.Post, Route, new
        {
            alias = "Repo One",
            hostPath = secretPath
        });
        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);
        var createJson = await createResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        var created = AssertEx.NotNull(JsonSerializer.Deserialize<WorkspaceBody>(createJson, JsonOptions));

        AssertEx.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        AssertEx.True(Guid.TryParse(created.WorkspaceId, out _), "The API must return an opaque workspace id.");
        AssertEx.Equal("repo-one", created.Alias);
        AssertEx.Equal("read-only", created.Mode);
        AssertEx.False(createJson.Contains(secretPath, StringComparison.Ordinal), "The create response must never expose the host path.");

        using var listRequest = OperatorRequest(factory, HttpMethod.Get, Route);
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);
        var listJson = await listResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        var list = AssertEx.NotNull(JsonSerializer.Deserialize<WorkspaceListBody>(listJson, JsonOptions));

        AssertEx.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        AssertEx.Equal(expected: 1, list.Items.Count);
        AssertEx.Equal(created, list.Items[0]);
        AssertEx.False(listJson.Contains(secretPath, StringComparison.Ordinal), "The list response must never expose the host path.");
        AssertEx.False(listJson.Contains("hostPath", StringComparison.OrdinalIgnoreCase), "The list schema must not contain a host-path property.");
    }

    [Test]
    public async Task Create_WhenAliasIsAlreadyRegistered_ReturnsConflict()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateClient();
        using var firstRequest = OperatorRequest(factory, HttpMethod.Post, Route, new
        {
            alias = "repo-one",
            hostPath = HostPath("trusted", "repo")
        });
        using var first = await client.SendAsync(firstRequest).ConfigureAwait(false);

        // Same alias after normalization, different host path: a collision, not a malformed request.
        using var duplicateRequest = OperatorRequest(factory, HttpMethod.Post, Route, new
        {
            alias = "Repo One",
            hostPath = HostPath("trusted", "other")
        });
        using var duplicate = await client.SendAsync(duplicateRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, first.StatusCode);
        AssertEx.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    // The 400 arm of the selected-folder triad on this route. The conflict test above covers 409 and the endpoint
    // shares one mapper with the Development routes, so the aggregate type must not answer 409 or 500 here.
    [Test]
    public async Task Create_WhenHostPathIsRelative_ReturnsBadRequest()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateClient();
        using var request = OperatorRequest(factory, HttpMethod.Post, Route, new
        {
            alias = "repo-one",
            hostPath = "relative/path"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenWorkspaceIdIsMalformed_ReturnsBadRequest()
    {
        await using var factory = CreateFactory(out _);
        using var client = factory.CreateClient();
        using var request = OperatorRequest(factory, HttpMethod.Delete, $"{Route}/not-a-guid");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenRepeated_IsIdempotentAndPreparationRunsOnlyForTheActiveRow()
    {
        await using var factory = CreateFactory(out var preparation);
        using var client = factory.CreateClient();
        using var createRequest = OperatorRequest(factory, HttpMethod.Post, Route, new
        {
            alias = "repo",
            hostPath = HostPath("trusted", "repo")
        });
        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);
        var created = AssertEx.NotNull(await createResponse.Content.ReadFromJsonAsync<WorkspaceBody>(JsonOptions).ConfigureAwait(false));

        using var firstRequest = OperatorRequest(factory, HttpMethod.Delete, $"{Route}/{created.WorkspaceId}");
        using var first = await client.SendAsync(firstRequest).ConfigureAwait(false);
        using var secondRequest = OperatorRequest(factory, HttpMethod.Delete, $"{Route}/{created.WorkspaceId}");
        using var second = await client.SendAsync(secondRequest).ConfigureAwait(false);
        using var unknownRequest = OperatorRequest(factory, HttpMethod.Delete, $"{Route}/{Guid.NewGuid()}");
        using var unknown = await client.SendAsync(unknownRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, first.StatusCode);
        AssertEx.Equal(HttpStatusCode.NoContent, second.StatusCode);
        AssertEx.Equal(second.StatusCode, unknown.StatusCode);
        _ = preparation.Received(1).PrepareAsync(Arg.Is<ResolvedSelectedFolder>(folder => folder.Id.ToString() == created.WorkspaceId),
            Arg.Any<CancellationToken>());

        using var listRequest = OperatorRequest(factory, HttpMethod.Get, Route);
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);
        var list = AssertEx.NotNull(await listResponse.Content.ReadFromJsonAsync<WorkspaceListBody>(JsonOptions).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, list.Items.Count);
    }

    [Test]
    public async Task Delete_WhenWorkspacePreparationFails_DoesNotReportSuccessOrHideTheWorkspace()
    {
        await using var factory = CreateFactory(out var preparation);
        using var client = factory.CreateClient();
        preparation.PrepareAsync(Arg.Any<ResolvedSelectedFolder>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromException<IWorkspaceRevocationSession>(new InvalidOperationException("clear failed")));
        using var createRequest = OperatorRequest(factory, HttpMethod.Post, Route, new
        {
            alias = "repo",
            hostPath = HostPath("trusted", "repo")
        });
        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);
        var created = AssertEx.NotNull(await createResponse.Content.ReadFromJsonAsync<WorkspaceBody>(JsonOptions).ConfigureAwait(false));

        using var deleteRequest = OperatorRequest(factory, HttpMethod.Delete, $"{Route}/{created.WorkspaceId}");
        using var deleteResponse = await client.SendAsync(deleteRequest).ConfigureAwait(false);

        AssertEx.True(deleteResponse.StatusCode is not HttpStatusCode.NoContent,
            "The endpoint must not advertise a successful revoke when workspace clear fails.");

        using var listRequest = OperatorRequest(factory, HttpMethod.Get, Route);
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);
        var list = AssertEx.NotNull(await listResponse.Content.ReadFromJsonAsync<WorkspaceListBody>(JsonOptions).ConfigureAwait(false));
        AssertEx.Equal(expected: 1, list.Items.Count);
        AssertEx.Equal(created.WorkspaceId, list.Items[0].WorkspaceId);
    }

    /// <summary>
    ///     The busy-lease 409 is written by the global <c>ConflictExceptionHandler</c>, not by the endpoint: the body is
    ///     the shared ConflictProblemDetails envelope discriminated by <c>conflictType</c>.
    /// </summary>
    [Test]
    public async Task Delete_WhenWorkspaceLeaseIsBusy_ReturnsConflictProblemDetails()
    {
        var revocation = Substitute.For<IWorkspaceRevocationService>();
        revocation.RevokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromException(new WorkspaceRevocationBusyException()));
        await using var factory = CreateFactory(out _, revocation);
        using var client = factory.CreateClient();
        using var request = OperatorRequest(factory, HttpMethod.Delete, $"{Route}/{Guid.NewGuid()}");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<WorkspaceConflictBody>(JsonOptions).ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.ToString(), "problem+json", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal("WorkspaceRevocationBusy", body.ConflictType);
        AssertEx.NotEmpty(body.Detail);
        AssertEx.NotEmpty(body.TraceId);
    }

    private static TestServerWebAppFactory CreateFactory(out IWorkspaceRevocationPreparation preparation,
        IWorkspaceRevocationService? revocationService = null)
    {
        preparation = Substitute.For<IWorkspaceRevocationPreparation>();
        var capturedPreparation = preparation;
        var session = Substitute.For<IWorkspaceRevocationSession>();
        capturedPreparation.PrepareAsync(Arg.Any<ResolvedSelectedFolder>(), Arg.Any<CancellationToken>()).Returns(session);
        var apiKeyService = Substitute.For<IMcpServerApiKeyService>();
        apiKeyService.ValidateAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(call => string.Equals(call.Arg<string?>(), McpKey, StringComparison.Ordinal)
                         ? new McpServerApiKeyValidation(McpServerApiKeyScope.Delegate, "xemcp_workspace")
                         : null);

        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWorkspaceRevocationPreparation>();
                services.AddScoped(_ => capturedPreparation);
                services.RemoveAll<IMcpServerApiKeyService>();
                services.AddScoped(_ => apiKeyService);
                if (revocationService is not null)
                {
                    services.RemoveAll<IWorkspaceRevocationService>();
                    services.AddScoped(_ => revocationService);
                }
            }
        };
    }

    private static HttpRequestMessage OperatorRequest(TestServerWebAppFactory factory, HttpMethod method, string route, object? body = null)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static string HostPath(params string[] segments) =>
        OperatingSystem.IsWindows()
            ? string.Concat(@"C:\", string.Join('\\', segments))
            : string.Concat("/", string.Join('/', segments));

    private sealed record WorkspaceBody(string WorkspaceId, string Alias, string Mode);

    private sealed record WorkspaceListBody(IReadOnlyList<WorkspaceBody> Items);

    private sealed record WorkspaceConflictBody(string ConflictType, string Detail, string TraceId);
}
