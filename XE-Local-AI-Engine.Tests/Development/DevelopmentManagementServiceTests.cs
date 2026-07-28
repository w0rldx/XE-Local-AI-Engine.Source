namespace XE_Local_AI_Engine.Tests.Development;

using System.Text;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

public sealed class DevelopmentManagementServiceTests
{
    [Test]
    public async Task StartNextAction_WhenReviewRoundCapReached_BlocksWithoutSchedulingValidation()
    {
        var repositoryRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(Directory.GetCurrentDirectory());
        var selectedFolderId = Guid.NewGuid();
        var projectId = Guid.Parse("63f0fcae-401e-4ff7-9be4-01de61427e65");
        var taskId = Guid.Parse("0db3ad35-a679-4a66-94ed-aa00bb52e0bb");
        var store = Substitute.For<IDevelopmentStore>();
        store.GetProjectAsync(projectId, Arg.Any<CancellationToken>())
             .Returns(ProjectSnapshot(projectId, selectedFolderId, DevelopmentWorkspaceSecurity.RepositoryIdentityHash(repositoryRoot)));
        store.GetTaskAsync(taskId, Arg.Any<CancellationToken>())
             .Returns(TaskSnapshot(projectId, taskId));
        store.FindOperationAsync(projectId,
                 Arg.Any<Guid>(),
                 DevelopmentOperationPhases.Completed,
                 Arg.Any<CancellationToken>())
             .Returns((DevelopmentOperationResult?)null);
        store.ListAttemptsAsync(taskId, Arg.Any<CancellationToken>())
             .Returns([
                 new DevelopmentAttemptSnapshot(Guid.NewGuid(),
                     taskId,
                     null,
                     DevelopmentAttemptRole.Coder,
                     "coder-model",
                     "local",
                     PersistenceAttemptStatus.Succeeded,
                     1,
                     2,
                     null,
                     10,
                     10,
                     1)
             ]);
        var coordinator = Substitute.For<IDevelopmentCoordinator>();
        var transitions = new List<DevelopmentTransitionTaskCommand>();
        coordinator.TransitionTaskAsync(Arg.Do<DevelopmentTransitionTaskCommand>(transitions.Add), Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       var command = call.ArgAt<DevelopmentTransitionTaskCommand>(0);
                       return new DevelopmentOperationResult(projectId,
                           taskId,
                           null,
                           null,
                           command.OperationId,
                           DevelopmentOperationPhases.Completed,
                           "Transitioned",
                           command.TargetStatus.ToString(),
                           command.ExpectedTaskVersion + 1,
                           1);
                   });
        var supervisor = Substitute.For<IDevelopmentAttemptExecutionSupervisor>();
        var repositoryBindings = Substitute.For<IDevelopmentRepositoryBindingService>();
        repositoryBindings.ResolveProjectAsync(projectId, Arg.Any<CancellationToken>())
                          .Returns(new DevelopmentRepositoryBinding(projectId,
                              selectedFolderId,
                              "repository",
                              repositoryRoot,
                              DevelopmentWorkspaceSecurity.RepositoryIdentityHash(repositoryRoot)));
        var service = CreateService(store, coordinator, supervisor, repositoryBindings);

        var result = await service.StartNextActionAsync(projectId,
            taskId,
            Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal("Blocked", result.Action);
        AssertEx.Equal(DevelopmentTaskStatus.Blocked, result.TaskStatus);
        AssertEx.Equal(expected: 1, transitions.Count);
        AssertEx.Equal(DevelopmentTaskStatus.Blocked, transitions[0].TargetStatus);
        AssertEx.Contains(AssertEx.NotNull(transitions[0].Reason), "maximum review rounds");
        _ = supervisor.DidNotReceive().StartValidation(Arg.Any<Guid>());
    }

    [Test]
    public async Task CreateProject_RetryWithSameOperationId_ReusesProjectAndTaskIdentity()
    {
        var selectedFolderId = Guid.NewGuid();
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
             .Returns(call => ProjectSnapshot(call.ArgAt<Guid>(0), selectedFolderId));
        store.ListTasksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<DevelopmentTaskSnapshot>());
        store.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<DevelopmentEventSnapshot>());

        var service = CreateService(store,
            coordinator,
            Substitute.For<IDevelopmentAttemptExecutionSupervisor>(),
            repositoryBindings: null,
            selectedFolderId: selectedFolderId);
        var operationId = Guid.Parse("8e9db44b-b50f-42c9-9bd0-3239af1eb5d8");
        var input = new DevelopmentCreateProjectInput(operationId,
            selectedFolderId,
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

    private static DevelopmentManagementService CreateService(IDevelopmentStore store,
        IDevelopmentCoordinator coordinator,
        IDevelopmentAttemptExecutionSupervisor supervisor,
        IDevelopmentRepositoryBindingService? repositoryBindings = null,
        Guid? selectedFolderId = null) =>
        new(store,
            coordinator,
            supervisor,
            Substitute.For<IDevelopmentArtifactBlobStore>(),
            new UnusedApplyService(),
            repositoryBindings ?? RepositoryBindings(selectedFolderId ?? Guid.NewGuid()),
            Substitute.For<IActiveCloudChatClientFactory>(),
            new GenericGitDetector(),
            ProfileBackfill(),
            TimeProvider.System);

    /// <summary>Backfill is a no-op here: every project these tests build already carries a profile.</summary>
    private static IDevelopmentProfileBackfillService ProfileBackfill()
    {
        var backfill = Substitute.For<IDevelopmentProfileBackfillService>();
        backfill.EnsureAsync(Arg.Any<DevelopmentProjectSnapshot>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<DevelopmentProjectSnapshot>());
        return backfill;
    }

    private static IDevelopmentRepositoryBindingService RepositoryBindings(Guid selectedFolderId)
    {
        var repositoryBindings = Substitute.For<IDevelopmentRepositoryBindingService>();
        repositoryBindings.ResolveFolderAsync(selectedFolderId, Arg.Any<CancellationToken>())
                          .Returns(new DevelopmentRepositoryBinding(Guid.Empty,
                              selectedFolderId,
                              "repository",
                              Directory.GetCurrentDirectory(),
                              "repository-hash"));
        return repositoryBindings;
    }

    private static DevelopmentProjectSnapshot ProjectSnapshot(Guid projectId,
        Guid? selectedFolderId = null,
        string repositoryIdentityHash = "repository-hash") =>
        new(projectId,
            "objective",
            selectedFolderId,
            repositoryIdentityHash,
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
            1,
            Encoding.UTF8.GetString(DevelopmentCommandProfileCatalog
                                    .Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null)
                                    .ToCanonicalUtf8()));

    private static DevelopmentTaskSnapshot TaskSnapshot(Guid projectId, Guid taskId) =>
        new(taskId,
            projectId,
            "task",
            "requirements",
            "[]",
            DevelopmentTaskStatus.InProgress,
            CurrentReviewRound: 3,
            MaxReviewRounds: 3,
            BlockedReason: null,
            BlockedAtUtc: null,
            ApprovedSubjectHash: null,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Version: 7);

    /// <summary>
    ///     Detection is stubbed rather than run for real: these tests bind the test host's own working directory as
    ///     the repository, so a real detector's answer would depend on what happens to sit in the build output.
    ///     <c>generic-git</c> is the honest stand-in — it is what detection returns for a directory with no .NET build
    ///     system, and it needs no build target.
    ///     <para>
    ///         Hand-written rather than an NSubstitute proxy because <c>IDevelopmentCommandProfileDetector</c> is
    ///         internal and Castle DynamicProxy cannot proxy it: <c>Client.Application</c> exposes its internals to
    ///         this assembly but not to <c>DynamicProxyGenAssembly2</c>.
    ///     </para>
    /// </summary>
    private sealed class GenericGitDetector : IDevelopmentCommandProfileDetector
    {
        public DevelopmentProfileDetection Detect(string repositoryRoot) =>
            new(DevelopmentCommandProfileCatalog.GenericGit, BuildTarget: null, []);
    }

    private sealed class UnusedApplyService : IDevelopmentApplyService
    {
        public Task<DevelopmentPatchPreview> PreviewAsync(Guid taskId,
            DevelopmentRepositoryBinding repository,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentOperationResult> ApplyAsync(Guid taskId,
            Guid operationId,
            DevelopmentRepositoryBinding repository,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
