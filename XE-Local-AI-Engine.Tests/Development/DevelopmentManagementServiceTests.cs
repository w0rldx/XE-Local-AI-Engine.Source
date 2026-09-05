namespace XE_Local_AI_Engine.Tests.Development;

using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

public sealed class DevelopmentManagementServiceTests
{
    /// <summary>
    ///     The budget is checked before the branch that would spend it, in BOTH states a task can be waiting in.
    ///     <para>
    ///         <c>ChangesRequested</c> is the one the deterministic gate's failure now leaves a task in, and the check
    ///         used to live inside the <c>InProgress</c> branch alone — so a task at its cap ran one more full coder
    ///         attempt, spending its tokens and its duration, and was stood down the tick after on work that could
    ///         never have reached a review.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(DevelopmentTaskStatus.InProgress)]
    [Arguments(DevelopmentTaskStatus.ChangesRequested)]
    public async Task StartNextAction_WhenTheRoundBudgetIsGone_BlocksWithoutSpendingAnother(DevelopmentTaskStatus waitingIn)
    {
        var repositoryRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(Directory.GetCurrentDirectory());
        var selectedFolderId = Guid.NewGuid();
        var projectId = Guid.Parse("63f0fcae-401e-4ff7-9be4-01de61427e65");
        var taskId = Guid.Parse("0db3ad35-a679-4a66-94ed-aa00bb52e0bb");
        var store = Substitute.For<IDevelopmentStore>();
        store.GetProjectAsync(projectId, Arg.Any<CancellationToken>())
             .Returns(ProjectSnapshot(projectId, selectedFolderId, DevelopmentWorkspaceSecurity.RepositoryIdentityHash(repositoryRoot)));
        store.GetTaskAsync(taskId, Arg.Any<CancellationToken>())
             .Returns(TaskSnapshot(projectId, taskId) with
             {
                 Status = waitingIn
             });
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
        AssertEx.Contains(AssertEx.NotNull(transitions[0].Reason), "maximum number of rounds");
        _ = supervisor.DidNotReceive().StartValidation(Arg.Any<Guid>());
        _ = supervisor.DidNotReceive().StartAttempt(Arg.Any<Guid>(), Arg.Any<DevelopmentAttemptRole>());
        await coordinator.DidNotReceive().StartAttemptAsync(Arg.Any<DevelopmentStartAttemptCommand>(), Arg.Any<CancellationToken>());
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

    [Test]
    [Arguments(DevelopmentEgressPolicy.LocalOnly)]
    [Arguments(DevelopmentEgressPolicy.CloudScoped)]
    public async Task StartNextAction_WithADeclaredCloudExternalModel_IsRefusedUnderEitherEgressPolicy(DevelopmentEgressPolicy egressPolicy)
    {
        // An ext: id is invisible to the cloud provider resolver — it falls THROUGH cloud selection by design — so
        // without its own check the LocalOnly guard below would have waved a hosted endpoint straight through. It is
        // refused under CloudScoped too: the plan admits no dev-mode support for a declared-cloud external model.
        var (store, coordinator, repositoryBindings, projectId, taskId) = ExternalModelFixture("ext:cloud-box/qwen3", egressPolicy);
        var trust = new FakeModelTrustResolver().Register("cloud-box", "qwen3", ExternalProviderLocality.Cloud);
        var service = CreateService(store,
            coordinator,
            Substitute.For<IDevelopmentAttemptExecutionSupervisor>(),
            repositoryBindings,
            modelTrustResolver: trust);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
            service.StartNextActionAsync(projectId, taskId, Guid.NewGuid()));
    }

    [Test]
    public async Task StartNextAction_WithAnUnresolvedExternalModel_IsRefused()
    {
        // A connection deleted mid-task, or a store that will not decrypt, says nothing about where the prompt would
        // have gone — so it is refused alongside the declared-cloud case.
        var (store, coordinator, repositoryBindings, projectId, taskId) = ExternalModelFixture("ext:gone/qwen3", DevelopmentEgressPolicy.LocalOnly);
        var service = CreateService(store,
            coordinator,
            Substitute.For<IDevelopmentAttemptExecutionSupervisor>(),
            repositoryBindings,
            modelTrustResolver: new FakeModelTrustResolver());

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
            service.StartNextActionAsync(projectId, taskId, Guid.NewGuid()));
    }

    [Test]
    public async Task StartNextAction_WithADeclaredLocalExternalModel_StartsTheAttempt()
    {
        // The other half of the locked decision: a self-hosted endpoint the operator declared Local is inside the
        // trust boundary and may drive a Development attempt.
        var (store, coordinator, repositoryBindings, projectId, taskId) = ExternalModelFixture("ext:local-box/qwen3", DevelopmentEgressPolicy.LocalOnly);
        var supervisor = Substitute.For<IDevelopmentAttemptExecutionSupervisor>();
        _ = supervisor.StartAttempt(Arg.Any<Guid>(), Arg.Any<DevelopmentAttemptRole>()).Returns(true);
        var trust = new FakeModelTrustResolver().Register("local-box", "qwen3");
        var service = CreateService(store, coordinator, supervisor, repositoryBindings, modelTrustResolver: trust);

        var result = await service.StartNextActionAsync(projectId, taskId, Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal("Attempt", result.Action);
    }

    /// <summary>
    ///     The project read always has the whole task list, so it asks for every task's workflow pointer ONCE. Asking
    ///     per task was a round trip per row on the page that renders most often, and the single-task read still
    ///     answers the single-task question.
    /// </summary>
    [Test]
    public async Task GetProject_ResolvesEveryTasksWorkflowRunIdInOneStoreCall()
    {
        var projectId = Guid.NewGuid();
        var operatorDriven = Guid.NewGuid();
        var workflowDriven = Guid.NewGuid();
        var alsoOperatorDriven = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var store = Substitute.For<IDevelopmentStore>();
        store.GetProjectAsync(projectId, Arg.Any<CancellationToken>()).Returns(ProjectSnapshot(projectId));
        store.ListTasksAsync(projectId, Arg.Any<CancellationToken>())
             .Returns([
                 TaskSnapshot(projectId, operatorDriven),
                 TaskSnapshot(projectId, workflowDriven),
                 TaskSnapshot(projectId, alsoOperatorDriven)
             ]);
        store.ListAttemptsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<DevelopmentAttemptSnapshot>());
        store.ListArtifactsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<DevelopmentArtifactSnapshot>());
        store.ListEventsAsync(projectId, Arg.Any<CancellationToken>()).Returns(Array.Empty<DevelopmentEventSnapshot>());

        var workflows = Substitute.For<IDevWorkflowStore>();
        workflows.FindRunIdsForDevelopmentTasksAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<Guid, Guid>
                 {
                     [workflowDriven] = runId
                 });

        var service = CreateService(store,
            Substitute.For<IDevelopmentCoordinator>(),
            Substitute.For<IDevelopmentAttemptExecutionSupervisor>(),
            workflows: workflows);

        var project = await service.GetProjectAsync(projectId);

        AssertEx.Equal(3, project.Tasks.Count);
        AssertEx.Null(project.Tasks[0].WorkflowRunId);
        AssertEx.Equal(runId, project.Tasks[1].WorkflowRunId!.Value);
        AssertEx.Null(project.Tasks[2].WorkflowRunId);
        await workflows.Received(1)
                       .FindRunIdsForDevelopmentTasksAsync(Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 3 && ids.Contains(workflowDriven)),
                           Arg.Any<CancellationToken>());
        await workflows.DidNotReceive().FindRunIdForDevelopmentTaskAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A Ready task on a project whose coder model is <paramref name="coderModelId" />, stubbed only as far as the model gate.</summary>
    private static (IDevelopmentStore Store, IDevelopmentCoordinator Coordinator, IDevelopmentRepositoryBindingService Bindings, Guid ProjectId, Guid TaskId)
        ExternalModelFixture(string coderModelId, DevelopmentEgressPolicy egressPolicy)
    {
        var repositoryRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(Directory.GetCurrentDirectory());
        var selectedFolderId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var store = Substitute.For<IDevelopmentStore>();
        _ = store.GetProjectAsync(projectId, Arg.Any<CancellationToken>())
                 .Returns(ProjectSnapshot(projectId, selectedFolderId, DevelopmentWorkspaceSecurity.RepositoryIdentityHash(repositoryRoot)) with
                 {
                     EgressPolicy = egressPolicy,
                     CoderModelId = coderModelId
                 });
        _ = store.GetTaskAsync(taskId, Arg.Any<CancellationToken>())
                 .Returns(TaskSnapshot(projectId, taskId) with
                 {
                     Status = DevelopmentTaskStatus.Ready,
                     CurrentReviewRound = 0
                 });
        _ = store.FindOperationAsync(projectId, Arg.Any<Guid>(), DevelopmentOperationPhases.Completed, Arg.Any<CancellationToken>())
                 .Returns((DevelopmentOperationResult?)null);
        _ = store.ListAttemptsAsync(taskId, Arg.Any<CancellationToken>())
                 .Returns(Array.Empty<DevelopmentAttemptSnapshot>());

        var coordinator = Substitute.For<IDevelopmentCoordinator>();
        _ = coordinator.StartAttemptAsync(Arg.Any<DevelopmentStartAttemptCommand>(), Arg.Any<CancellationToken>())
                       .Returns(call =>
                       {
                           var command = call.ArgAt<DevelopmentStartAttemptCommand>(0);
                           return new DevelopmentOperationResult(projectId,
                               taskId,
                               command.AttemptId,
                               null,
                               command.OperationId,
                               DevelopmentOperationPhases.Completed,
                               "Started",
                               DevelopmentTaskStatus.InProgress.ToString(),
                               command.ExpectedTaskVersion + 1,
                               1);
                       });

        var repositoryBindings = Substitute.For<IDevelopmentRepositoryBindingService>();
        _ = repositoryBindings.ResolveProjectAsync(projectId, Arg.Any<CancellationToken>())
                              .Returns(new DevelopmentRepositoryBinding(projectId,
                                  selectedFolderId,
                                  "repository",
                                  repositoryRoot,
                                  DevelopmentWorkspaceSecurity.RepositoryIdentityHash(repositoryRoot)));

        return (store, coordinator, repositoryBindings, projectId, taskId);
    }

    private static DevelopmentManagementService CreateService(IDevelopmentStore store,
        IDevelopmentCoordinator coordinator,
        IDevelopmentAttemptExecutionSupervisor supervisor,
        IDevelopmentRepositoryBindingService? repositoryBindings = null,
        Guid? selectedFolderId = null,
        IModelTrustResolver? modelTrustResolver = null,
        IDevWorkflowStore? workflows = null) =>
        new(store,
            coordinator,
            supervisor,
            Substitute.For<IDevelopmentArtifactBlobStore>(),
            new UnusedApplyService(),
            repositoryBindings ?? RepositoryBindings(selectedFolderId ?? Guid.NewGuid()),
            Substitute.For<IActiveCloudChatClientFactory>(),
            modelTrustResolver ?? new FakeModelTrustResolver(),
            new GenericGitDetector(),
            ProfileBackfill(),

            // No materialization for these projects: they are registered repositories, not template-created ones, so
            // the profile carries no template id.
            Substitute.For<IDevelopmentTemplateStore>(),

            // No workflow drives these tasks: the substitute answers null, which is the ordinary operator-driven case.
            workflows ?? Substitute.For<IDevWorkflowStore>(),

            // Workflows ON, so the apply-ownership guard is the one under test wherever these tests reach it.
            Options.Create(new DevWorkflowOptions
            {
                Enabled = true
            }),
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
