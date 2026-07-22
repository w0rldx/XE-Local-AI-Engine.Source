namespace XE_Local_AI_Engine.Tests.Development;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentManagementServiceTests
{
    [Test]
    public async Task CreateProject_RetryWithSameOperationId_ReusesProjectAndTaskIdentity()
    {
        var store = Substitute.For<IDevelopmentStore>();
        var coordinator = Substitute.For<IDevelopmentCoordinator>();
        var commands = new List<DevelopmentCreateProjectCommand>();

        coordinator.CreateProjectAsync(Arg.Do<DevelopmentCreateProjectCommand>(commands.Add), Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       var command = call.ArgAt<DevelopmentCreateProjectCommand>(0);
                       return new DevelopmentOperationResult(command.ProjectId,
                           command.TaskId,
                           null,
                           null,
                           command.OperationId,
                           DevelopmentOperationPhases.Completed,
                           "Created",
                           DevelopmentTaskStatus.Planned.ToString(),
                           1,
                           1);
                   });
        store.GetProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(call => ProjectSnapshot(call.ArgAt<Guid>(0)));
        store.ListTasksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<DevelopmentTaskSnapshot>());
        store.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<DevelopmentEventSnapshot>());

        var service = new DevelopmentManagementService(store,
            coordinator,
            Substitute.For<IDevelopmentAttemptExecutionSupervisor>(),
            Substitute.For<IDevelopmentArtifactBlobStore>(),
            new UnusedApplyService(),
            Substitute.For<IActiveCloudChatClientFactory>(),
            TimeProvider.System);
        var operationId = Guid.Parse("8e9db44b-b50f-42c9-9bd0-3239af1eb5d8");
        var input = new DevelopmentCreateProjectInput(operationId,
            Directory.GetCurrentDirectory(),
            "Implement the durable workflow",
            "main",
            "Implement task",
            "Keep the operation idempotent",
            "[]",
            DevelopmentEgressPolicy.LocalOnly,
            "coder-model",
            "reviewer-model",
            TrustedRepositoryAcknowledged: true);

        var first = await service.CreateProjectAsync(input).ConfigureAwait(false);
        var retry = await service.CreateProjectAsync(input).ConfigureAwait(false);

        AssertEx.Equal(2, commands.Count);
        AssertEx.Equal(commands[0].ProjectId, commands[1].ProjectId);
        AssertEx.Equal(commands[0].TaskId, commands[1].TaskId);
        AssertEx.Equal(operationId, commands[0].OperationId);
        AssertEx.Equal(first.Project.Id, retry.Project.Id);
    }

    private static DevelopmentProjectSnapshot ProjectSnapshot(Guid projectId)
        => new(projectId,
            "objective",
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
            DevelopmentTrustPolicy.CurrentVersion,
            1,
            1,
            1,
            1);

    private sealed class UnusedApplyService : IDevelopmentApplyService
    {
        public Task<DevelopmentPatchPreview> PreviewAsync(Guid taskId,
            string repositoryRoot,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DevelopmentOperationResult> ApplyAsync(Guid taskId,
            Guid operationId,
            string repositoryRoot,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
