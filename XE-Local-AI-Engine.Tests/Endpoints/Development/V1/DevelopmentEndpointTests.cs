namespace XE_Local_AI_Engine.Tests.Endpoints.Development.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel("DevelopmentFeatureConfiguration")]
public sealed class DevelopmentEndpointTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    [Arguments("GET", "/api/local/v1/development/capability")]
    [Arguments("GET", "/api/local/v1/development/projects")]
    [Arguments("POST", "/api/local/v1/development/projects")]
    [Arguments("GET", "/api/local/v1/development/repositories")]
    [Arguments("POST", "/api/local/v1/development/repositories")]
    [Arguments("GET", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111")]
    [Arguments("GET", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/tasks/22222222-2222-2222-2222-222222222222")]
    [Arguments("POST", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/tasks/22222222-2222-2222-2222-222222222222/next-action")]
    [Arguments("POST", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/tasks/22222222-2222-2222-2222-222222222222/attempts/33333333-3333-3333-3333-333333333333/cancel")]
    [Arguments("GET", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/events")]
    [Arguments("GET", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/tasks/22222222-2222-2222-2222-222222222222/artifacts")]
    [Arguments("GET", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/tasks/22222222-2222-2222-2222-222222222222/artifacts/44444444-4444-4444-4444-444444444444")]
    [Arguments("POST", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/tasks/22222222-2222-2222-2222-222222222222/preview")]
    [Arguments("POST", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/tasks/22222222-2222-2222-2222-222222222222/apply")]
    [Arguments("POST", "/api/local/v1/development/projects/11111111-1111-1111-1111-111111111111/repository-connection")]
    public async Task ManagementRoute_WhenOperatorTokenIsMissing_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = true
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
    }

    [Test]
    public async Task GetProject_WithOnlyRouteProjectId_BindsProjectIdAndReturnsOk()
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(ProjectAggregate(ProjectId));
        await using var factory = EnabledFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/development/projects/{ProjectId}");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await service.Received(1).GetProjectAsync(ProjectId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetTask_WithOnlyRouteProjectAndTaskIds_BindsBothIdsAndReturnsOk()
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>()).Returns(TaskAggregate(ProjectId, TaskId, []));
        await using var factory = EnabledFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/development/projects/{ProjectId}/tasks/{TaskId}");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await service.Received(1).GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterRepository_WhenSuccessful_DoesNotExposeHostPath()
    {
        const string HostPath = "/secret/operator/repository";
        var service = Substitute.For<IDevelopmentManagementService>();
        service.RegisterRepositoryAsync("repository", HostPath, Arg.Any<CancellationToken>())
               .Returns(new DevelopmentRepositoryReference("44444444-4444-4444-4444-444444444444", "repository", "Available"));
        await using var factory = EnabledFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/development/repositories")
        {
            Content = new StringContent("{\"alias\":\"repository\",\"hostPath\":\"/secret/operator/repository\"}",
                Encoding.UTF8,
                "application/json")
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(responseJson.Contains(HostPath, StringComparison.Ordinal));
        AssertEx.False(responseJson.Contains("hostPath", StringComparison.OrdinalIgnoreCase));
        AssertEx.Contains(responseJson, "repository", StringComparison.Ordinal);
    }

    // The selected-folder taxonomy is split by type, and the type — not the operation — carries the status:
    // unknown id → 404, alias conflict → 409, anything else → 400. Before the split every operation caught the one
    // aggregate exception and picked its own status, so an unknown id was 404 on start/preview/apply but 400 on
    // create/reconnect. These three tests pin the taxonomy instead of that inconsistency.
    [Test]
    [Arguments("create")]
    [Arguments("start")]
    [Arguments("preview")]
    [Arguments("apply")]
    [Arguments("reconnect")]
    public async Task Operation_WhenSelectedFolderIdIsUnknown_ReturnsNotFound(string operation)
    {
        using var response = await SendWithFailingServiceAsync(operation,
                                       new SelectedFolderNotFoundException("The selected folder id is not registered."))
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    [Arguments("create")]
    [Arguments("start")]
    [Arguments("preview")]
    [Arguments("apply")]
    [Arguments("reconnect")]
    public async Task Operation_WhenSelectedFolderInputIsRejected_ReturnsBadRequest(string operation)
    {
        using var response = await SendWithFailingServiceAsync(operation,
                                       new SelectedFolderValidationException("The selected folder id is not a valid identifier."))
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task CreateProject_WhenSelectedFolderAliasIsAlreadyRegistered_ReturnsConflict()
    {
        using var response = await SendWithFailingServiceAsync("create",
                                       new SelectedFolderConflictException("A selected folder with alias 'repo-one' is already registered."))
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendWithFailingServiceAsync(string operation, Exception failure)
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.CreateProjectAsync(Arg.Any<DevelopmentCreateProjectInput>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromException<DevelopmentProjectAggregate>(failure));
        service.StartNextActionAsync(ProjectId, TaskId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromException<DevelopmentNextActionResult>(failure));
        service.PreviewAsync(ProjectId, TaskId, Arg.Any<CancellationToken>())
               .Returns(Task.FromException<DevelopmentPatchPreviewResult>(failure));
        service.ApplyAsync(ProjectId, TaskId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromException<DevelopmentOperationResult>(failure));
        service.ReconnectRepositoryAsync(ProjectId, Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromException<DevelopmentProjectAggregate>(failure));
        await using var factory = EnabledFactory(service);
        using var client = factory.CreateClient();
        using var request = StaleSelectedFolderRequest(operation);
        factory.AddNodeBearerToken(request);

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    [Test]
    public void DevelopmentActionRequest_DoesNotExposeRepositoryRoot()
    {
        var propertyNames = typeof(DevelopmentActionRequest)
                            .GetProperties()
                            .Select(static property => property.Name)
                            .ToArray();

        AssertEx.False(propertyNames.Contains("RepositoryRoot", StringComparer.Ordinal));
    }

    private static TestServerWebAppFactory EnabledFactory(IDevelopmentManagementService service) =>
        new()
        {
            EnableDevelopmentMode = true,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IDevelopmentManagementService>();
                services.AddSingleton(service);
            }
        };

    private static HttpRequestMessage StaleSelectedFolderRequest(string operation) =>
        operation switch
        {
            "create" => new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/development/projects")
            {
                Content = JsonContent.Create(new CreateDevelopmentProjectRequest
                {
                    OperationId = Guid.NewGuid(),
                    SelectedFolderId = Guid.NewGuid(),
                    Objective = "objective",
                    TaskTitle = "task",
                    Requirements = "requirements",
                    CoderModelId = "coder-model",
                    ReviewerModelId = "reviewer-model",
                    TrustedRepositoryAcknowledged = true
                })
            },
            "start" => ActionRequest("next-action"),
            "preview" => ActionRequest("preview"),
            "apply" => ActionRequest("apply"),
            "reconnect" => new HttpRequestMessage(HttpMethod.Post, $"/api/local/v1/development/projects/{ProjectId}/repository-connection")
            {
                Content = JsonContent.Create(new ReconnectDevelopmentRepositoryRequest
                {
                    SelectedFolderId = Guid.NewGuid(),
                    ExpectedVersion = 1
                })
            },
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown Development operation.")
        };

    private static HttpRequestMessage ActionRequest(string action) =>
        new(HttpMethod.Post, $"/api/local/v1/development/projects/{ProjectId}/tasks/{TaskId}/{action}")
        {
            Content = JsonContent.Create(new DevelopmentActionRequest
            {
                OperationId = Guid.NewGuid()
            })
        };

    private static DevelopmentProjectAggregate ProjectAggregate(Guid projectId) =>
        new(new DevelopmentProjectSnapshot(projectId,
                "objective",
                Guid.NewGuid(),
                "repository-hash",
                "main",
                DevelopmentProjectStatus.Active,
                DevelopmentEgressPolicy.LocalOnly,
                "coder-model",
                "reviewer-model",
                null,
                null,
                1,
                true,
                1,
                1,
                1,
                1,
                1,
                CommandProfileJson: null),
            [],
            []);

    internal static DevelopmentTaskAggregate TaskAggregate(Guid projectId,
        Guid taskId,
        IReadOnlyList<DevelopmentAttemptSnapshot> attempts) =>
        new(new DevelopmentTaskSnapshot(taskId,
                projectId,
                "task",
                "requirements",
                "[]",
                DevelopmentTaskStatus.InProgress,
                0,
                3,
                null,
                null,
                null,
                1,
                1,
                1),
            attempts,
            []);
}
