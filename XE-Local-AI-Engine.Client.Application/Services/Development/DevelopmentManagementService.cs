namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;

public interface IDevelopmentManagementService
{
    Task<DevelopmentRepositoryReference> RegisterRepositoryAsync(string displayAlias,
        string hostPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevelopmentRepositoryReference>> ListRepositoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Proposes a command profile for a registered repository so the operator can confirm or override it before a
    ///     project is created. Read-only and non-authoritative — the confirmed choice is what gets snapshotted.
    /// </summary>
    Task<DevelopmentProfileDetectionResult> DetectRepositoryProfileAsync(Guid selectedFolderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevelopmentProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task<DevelopmentProjectAggregate> CreateProjectAsync(DevelopmentCreateProjectInput input, CancellationToken cancellationToken = default);
    Task<DevelopmentProjectAggregate> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<DevelopmentTaskAggregate> GetTaskAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default);

    Task<DevelopmentNextActionResult> StartNextActionAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<bool> CancelAttemptAsync(Guid projectId, Guid taskId, Guid attemptId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default);

    Task<DevelopmentArtifactContent> ReadArtifactAsync(Guid projectId,
        Guid taskId,
        Guid artifactId,
        CancellationToken cancellationToken = default);

    Task<DevelopmentPatchPreviewResult> PreviewAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>Puts a task's approved patch into the repository.</summary>
    /// <remarks>
    ///     <paramref name="onBehalfOfWorkflowRunId" /> names the development-workflow run whose apply lane is asking,
    ///     and is the only thing that gets an apply past a live run's ownership of that decision. An operator surface
    ///     passes <see langword="null" />, which makes the refusal server-side rather than a withheld button.
    /// </remarks>
    Task<DevelopmentOperationResult> ApplyAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
        Guid? onBehalfOfWorkflowRunId,
        CancellationToken cancellationToken = default);

    Task<DevelopmentProjectAggregate> ReconnectRepositoryAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentManagementService : IDevelopmentManagementService
{
    /// <summary>
    ///     Why a task with no rounds left was stood down and — being persisted as the task's reason, then read back to
    ///     recognise that stand-down — the sentinel for it.
    /// </summary>
    /// <remarks>
    ///     It says "rounds" rather than "review rounds" because the budget does not count review entries alone: a
    ///     failed deterministic gate spends one too.
    /// </remarks>
    private const string ReviewRoundLimitReason = "The configured maximum number of rounds has been reached.";

    private readonly IDevelopmentApplyService _applyService;
    private readonly IDevelopmentArtifactBlobStore _blobStore;
    private readonly IDevelopmentCoordinator _coordinator;

    /// <summary>
    ///     Records every task-status hop <see cref="StartNextActionAsync" /> decides.
    /// </summary>
    /// <remarks>
    ///     All three messages — <c>Planned → Ready</c>, the round start and the stand-down at the round cap — carry
    ///     the literal phrase "task status", so one grep finds every hop. Without them a claim about what the chain
    ///     did is model-quoted rather than a system record.
    /// </remarks>
    private readonly ILogger<DevelopmentManagementService> _logger;

    private readonly IActiveCloudChatClientFactory _cloudFactory;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly IDevelopmentProfileBackfillService _profileBackfill;
    private readonly IDevelopmentCommandProfileDetector _profileDetector;
    private readonly IDevelopmentRepositoryBindingService _repositoryBindings;
    private readonly IDevelopmentStore _store;
    private readonly IDevelopmentAttemptExecutionSupervisor _supervisor;
    private readonly IDevelopmentTemplateStore _templateStore;
    private readonly DevWorkflowOptions _workflowOptions;

    /// <summary>
    ///     Read-only, and for one question: which workflow run — if any — owns the approval for a task.
    /// </summary>
    /// <remarks>
    ///     Asked here rather than at the endpoint layer because this service is the one place a task aggregate is
    ///     built — the project detail loops back through <see cref="GetTaskAsync" /> for every task it carries — so
    ///     composing it above would mean asking at three call sites today and remembering the next one.
    /// </remarks>
    private readonly IDevWorkflowStore _workflows;

    private readonly TimeProvider _timeProvider;

    public DevelopmentManagementService(
        IDevelopmentStore store,
        IDevelopmentCoordinator coordinator,
        IDevelopmentAttemptExecutionSupervisor supervisor,
        IDevelopmentArtifactBlobStore blobStore,
        IDevelopmentApplyService applyService,
        IDevelopmentRepositoryBindingService repositoryBindings,
        IActiveCloudChatClientFactory cloudFactory,
        IModelTrustResolver modelTrustResolver,
        IDevelopmentCommandProfileDetector profileDetector,
        IDevelopmentProfileBackfillService profileBackfill,
        IDevelopmentTemplateStore templateStore,
        IDevWorkflowStore workflows,
        IOptions<DevWorkflowOptions> workflowOptions,
        TimeProvider timeProvider,
        ILogger<DevelopmentManagementService> logger)
    {
        ArgumentNullException.ThrowIfNull(applyService);
        _applyService = applyService;
        ArgumentNullException.ThrowIfNull(blobStore);
        _blobStore = blobStore;
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(cloudFactory);
        _cloudFactory = cloudFactory;
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        _modelTrustResolver = modelTrustResolver;
        ArgumentNullException.ThrowIfNull(profileBackfill);
        _profileBackfill = profileBackfill;
        ArgumentNullException.ThrowIfNull(profileDetector);
        _profileDetector = profileDetector;
        ArgumentNullException.ThrowIfNull(repositoryBindings);
        _repositoryBindings = repositoryBindings;
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        ArgumentNullException.ThrowIfNull(supervisor);
        _supervisor = supervisor;
        ArgumentNullException.ThrowIfNull(templateStore);
        _templateStore = templateStore;
        _workflowOptions = (workflowOptions ?? throw new ArgumentNullException(nameof(workflowOptions))).Value;
        ArgumentNullException.ThrowIfNull(workflows);
        _workflows = workflows;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public Task<DevelopmentRepositoryReference> RegisterRepositoryAsync(string displayAlias,
        string hostPath,
        CancellationToken cancellationToken = default) =>
        _repositoryBindings.RegisterAsync(displayAlias, hostPath, cancellationToken);

    public Task<IReadOnlyList<DevelopmentRepositoryReference>> ListRepositoriesAsync(CancellationToken cancellationToken = default) =>
        _repositoryBindings.ListAsync(cancellationToken);

    public async Task<DevelopmentProfileDetectionResult> DetectRepositoryProfileAsync(Guid selectedFolderId,
        CancellationToken cancellationToken = default)
    {
        var repository = await _repositoryBindings.ResolveFolderAsync(selectedFolderId, cancellationToken);
        var detected = _profileDetector.Detect(repository.RepositoryRoot);
        return new DevelopmentProfileDetectionResult { ProfileId = detected.ProfileId, BuildTarget = detected.BuildTarget, Candidates = detected.Candidates };
    }

    public Task<IReadOnlyList<DevelopmentProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        _store.ListProjectsAsync(cancellationToken);

    public async Task<DevelopmentProjectAggregate> CreateProjectAsync(DevelopmentCreateProjectInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Objective)
            || string.IsNullOrWhiteSpace(input.BaseBranch)
            || string.IsNullOrWhiteSpace(input.TaskTitle)
            || string.IsNullOrWhiteSpace(input.Requirements)
            || string.IsNullOrWhiteSpace(input.AcceptanceCriteriaJson)
            || string.IsNullOrWhiteSpace(input.CoderModelId)
            || string.IsNullOrWhiteSpace(input.ReviewerModelId))
        {
            throw new ArgumentException("Development project and task fields must not be blank.", nameof(input));
        }

        if (!input.TrustedRepositoryAcknowledged)
        {
            throw new DevelopmentWorkspaceSecurityException("Development execution requires explicit trusted-repository acknowledgement.");
        }

        var repository = await _repositoryBindings.ResolveFolderAsync(input.SelectedFolderId, cancellationToken);

        // The profile is snapshotted once here and never re-read from the worktree, which the agent can write. Template
        // provenance comes from the materialization record, so a client cannot assert which template a repository is.
        var materialization = await _templateStore.FindMaterializationAsync(input.SelectedFolderId, cancellationToken);
        var profile = ResolveCommandProfile(input, repository.RepositoryRoot, materialization?.TemplateId.ToString());
        var projectId = DerivedOperationId(input.OperationId, "project");
        var taskId = DerivedOperationId(input.OperationId, "task");
        _ = await _coordinator.CreateProjectAsync(new DevelopmentCreateProjectCommand
        {
            ProjectId = projectId,
            TaskId = taskId,
            OperationId = input.OperationId,
            Objective = input.Objective,
            SelectedFolderId = input.SelectedFolderId,
            RepositoryIdentityHash = repository.RepositoryIdentityHash,
            BaseBranch = input.BaseBranch,
            Title = input.TaskTitle,
            Requirements = input.Requirements,
            AcceptanceCriteriaJson = input.AcceptanceCriteriaJson,
            EgressPolicy = input.EgressPolicy,
            CoderModelId = input.CoderModelId,
            ReviewerModelId = input.ReviewerModelId,
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion,
            TrustedRepositoryAcknowledgedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            MaxTokens = input.MaxTokens,
            MaxDurationSeconds = input.MaxDurationSeconds,
            CommandProfileJson = Encoding.UTF8.GetString(profile.ToCanonicalUtf8())
        },
                                  cancellationToken);
        return await GetProjectAsync(projectId, cancellationToken);
    }

    /// <summary>
    ///     Resolves the profile to snapshot. An explicitly supplied id is the operator's confirmed choice and wins;
    ///     otherwise detection proposes one from the repository contents.
    /// </summary>
    private DevelopmentCommandProfile ResolveCommandProfile(DevelopmentCreateProjectInput input,
        string repositoryRoot,
        string? templateId)
    {
        // Read once, here, on the trusted host path. The digest of these exact bytes rides on the profile so the
        // workspace invariant can detect a command rewriting the file mid-attempt.
        var import = DevelopmentCommandProfileImport.TryRead(repositoryRoot);
        var importDigest = import?.Digest;

        // Precedence: the operator's confirmation, then the repository's request, then detection. That request is only
        // ever a choice among code-owned profiles — Materialize rejects anything else — so it selects, never defines.
        var profileId = !string.IsNullOrWhiteSpace(input.CommandProfileId)
            ? input.CommandProfileId
            : import?.Document.ProfileId;
        var buildTarget = !string.IsNullOrWhiteSpace(input.CommandProfileId)
            ? input.BuildTarget
            : import?.Document.BuildTarget;

        if (string.IsNullOrWhiteSpace(profileId))
        {
            var detected = _profileDetector.Detect(repositoryRoot);
            profileId = detected.ProfileId;
            buildTarget = detected.BuildTarget;
        }

        return DevelopmentCommandProfileCatalog.Materialize(profileId, buildTarget, templateId, importDigest);
    }

    public async Task<DevelopmentProjectAggregate> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(projectId, cancellationToken);

        // Legacy projects predate the profile column. Filling it here, rather than only at startup, means a repository
        // that was offline at boot becomes usable as soon as it is back — no restart. A no-op once the profile exists.
        project = await _profileBackfill.EnsureAsync(project, cancellationToken);
        var tasks = await _store.ListTasksAsync(projectId, cancellationToken);

        // ONE query for every task's workflow pointer, not one per task: this read always has the whole task list, so
        // asking the single-task question in a loop is a round trip per row on the page that renders most often.
        var workflowRunIds = await _workflows.FindRunIdsForDevelopmentTasksAsync([.. tasks.Select(static task => task.Id)], cancellationToken);
        var aggregates = new List<DevelopmentTaskAggregate>(tasks.Count);
        foreach (var task in tasks)
        {
            aggregates.Add(new DevelopmentTaskAggregate
            {
                Task = task,
                Attempts = await _store.ListAttemptsAsync(task.Id, cancellationToken),
                Artifacts = await _store.ListArtifactsAsync(task.Id, cancellationToken),
                WorkflowRunId = workflowRunIds.TryGetValue(task.Id, out var runId) ? runId : null
            });
        }

        return new DevelopmentProjectAggregate
        {
            Project = project,
            Tasks = aggregates,
            Events = await _store.ListEventsAsync(projectId, cancellationToken)
        };
    }

    public async Task<DevelopmentTaskAggregate> GetTaskAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await RequireTaskAsync(projectId, taskId, cancellationToken);

        // Read back through the pointer a DevTask node run stamps rather than stored on the task: the task row belongs
        // to Development Mode, and a workflow driving one is a fact about the workflow.
        return new DevelopmentTaskAggregate
        {
            Task = task,
            Attempts = await _store.ListAttemptsAsync(taskId, cancellationToken),
            Artifacts = await _store.ListArtifactsAsync(taskId, cancellationToken),
            WorkflowRunId = await _workflows.FindRunIdForDevelopmentTaskAsync(taskId, cancellationToken)
        };
    }

    public async Task<DevelopmentNextActionResult> StartNextActionAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(projectId, cancellationToken);
        DevelopmentTrustPolicy.EnsureCurrent(project, _timeProvider);
        _ = await _repositoryBindings.ResolveProjectAsync(projectId, cancellationToken);

        var existing = await _store.FindOperationAsync(projectId,
            operationId,
            DevelopmentOperationPhases.Completed,
            cancellationToken);
        if (existing?.AttemptId is { } existingAttemptId)
        {
            var existingAttempt = (await _store.ListAttemptsAsync(taskId, cancellationToken))
                .Single(attempt => attempt.Id == existingAttemptId);
            var existingTask = await RequireTaskAsync(projectId, taskId, cancellationToken);
            return new DevelopmentNextActionResult
            {
                Action = "Attempt",
                ProjectId = projectId,
                TaskId = taskId,
                AttemptId = existingAttemptId,
                TaskStatus = existingTask.Status,
                Role = existingAttempt.Role
            };
        }

        var task = await RequireTaskAsync(projectId, taskId, cancellationToken);
        if (task.Status == DevelopmentTaskStatus.Blocked
            && string.Equals(task.BlockedReason, ReviewRoundLimitReason, StringComparison.Ordinal))
        {
            return new DevelopmentNextActionResult { Action = "Blocked", ProjectId = projectId, TaskId = taskId, AttemptId = null, TaskStatus = DevelopmentTaskStatus.Blocked, Role = null };
        }

        if (task.Status == DevelopmentTaskStatus.Planned)
        {
            var ready = await _coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand
            {
                TaskId = taskId,
                OperationId = DerivedOperationId(operationId, "ready"),
                TargetStatus = DevelopmentTaskStatus.Ready,
                ExpectedTaskVersion = task.Version
            },
                                              cancellationToken);
            task = (await _store.GetTaskAsync(taskId, cancellationToken)) with
            {
                Version = ready.Version
            };
            _logger.LogInformation("Development task status moved Planned to Ready for task {TaskId} in project {ProjectId}.", taskId, projectId);
        }

        var attempts = await _store.ListAttemptsAsync(taskId, cancellationToken);
        if (attempts.Any(attempt => attempt.Status is DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running))
        {
            throw new DevelopmentInvalidTransitionException("The Development task already has an active attempt.");
        }

        // InProgress behind a SUCCEEDED coder attempt is the state that means "implemented, validate it".
        var awaitingValidation = task.Status == DevelopmentTaskStatus.InProgress
                                 && attempts.LastOrDefault(attempt => attempt.Role == DevelopmentAttemptRole.Coder) is { Status: DevelopmentAttemptStatus.Succeeded };

        // The budget is checked before the branch that would spend it, covering the rework and validation waits alike.
        // ChangesRequested is in, or a doomed coder round burns an attempt; InReview is out, being already paid for.
        if ((awaitingValidation || task.Status == DevelopmentTaskStatus.ChangesRequested)
            && task.CurrentReviewRound >= task.MaxReviewRounds)
        {
            _ = await _coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand
            {
                TaskId = taskId,
                OperationId = DerivedOperationId(operationId, "review-round-limit"),
                TargetStatus = DevelopmentTaskStatus.Blocked,
                ExpectedTaskVersion = task.Version,
                Reason = ReviewRoundLimitReason
            },
                                      cancellationToken);
            _logger.LogInformation("Development task status moved {From} to Blocked for task {TaskId} in project {ProjectId} after {Round} of {Max} rounds: {Reason}",
                task.Status,
                taskId,
                projectId,
                task.CurrentReviewRound,
                task.MaxReviewRounds,
                ReviewRoundLimitReason);
            return new DevelopmentNextActionResult { Action = "Blocked", ProjectId = projectId, TaskId = taskId, AttemptId = null, TaskStatus = DevelopmentTaskStatus.Blocked, Role = null };
        }

        if (awaitingValidation)
        {
            if (!_supervisor.StartValidation(taskId))
            {
                throw new DevelopmentConcurrencyException("Deterministic validation is already scheduled for this task.");
            }

            return new DevelopmentNextActionResult { Action = "Validation", ProjectId = projectId, TaskId = taskId, AttemptId = null, TaskStatus = task.Status, Role = null };
        }

        var role = task.Status switch
        {
            DevelopmentTaskStatus.Ready or DevelopmentTaskStatus.InProgress or DevelopmentTaskStatus.ChangesRequested => DevelopmentAttemptRole.Coder,
            DevelopmentTaskStatus.InReview => DevelopmentAttemptRole.Reviewer,
            _ => throw new DevelopmentInvalidTransitionException("The Development task has no executable next action in its current state.")
        };
        var modelId = role == DevelopmentAttemptRole.Coder ? project.CoderModelId : project.ReviewerModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new DevelopmentInvalidTransitionException("The Development role has no configured model.");
        }

        // An ext: id falls through cloud selection, so its locality is asked separately and refused under both egress
        // policies; UNRESOLVED too, because a deleted connection or unreadable store says nothing about the prompt.
        if (ExternalModelId.HasExternalScheme(modelId)
            && await _modelTrustResolver.ResolveAsync(modelId, cancellationToken) != ModelTrustLocality.Local)
        {
            throw new DevelopmentWorkspaceSecurityException("Development execution cannot start with an external model that is not declared local to this node's trust boundary.");
        }

        var cloudProvider = _cloudFactory.ResolveActiveCloudProviderName(modelId);
        if (project.EgressPolicy == DevelopmentEgressPolicy.LocalOnly && !string.IsNullOrWhiteSpace(cloudProvider))
        {
            throw new DevelopmentWorkspaceSecurityException("LocalOnly Development execution cannot start with a cloud-routed model.");
        }

        var provider = string.IsNullOrWhiteSpace(cloudProvider) ? "local" : cloudProvider;

        var predecessor = attempts.LastOrDefault(attempt => attempt.Role == role && attempt.Status == DevelopmentAttemptStatus.Interrupted)?.Id;
        var attemptId = Guid.NewGuid();
        _ = await _coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand
        {
            TaskId = taskId,
            AttemptId = attemptId,
            OperationId = operationId,
            Role = role,
            ModelId = modelId,
            Provider = provider,
            ExpectedTaskVersion = task.Version,
            PredecessorAttemptId = predecessor
        },
                                  cancellationToken);
        if (!_supervisor.StartAttempt(attemptId, role))
        {
            throw new DevelopmentConcurrencyException("The Development attempt is already scheduled.");
        }

        var startedTask = await _store.GetTaskAsync(taskId, cancellationToken);
        _logger.LogInformation("Development task status moved {From} to {To} for task {TaskId} in project {ProjectId}, starting a {Role} round.",
            task.Status,
            startedTask.Status,
            taskId,
            projectId,
            role);
        return new DevelopmentNextActionResult { Action = "Attempt", ProjectId = projectId, TaskId = taskId, AttemptId = attemptId, TaskStatus = startedTask.Status, Role = role };
    }

    public async Task<bool> CancelAttemptAsync(Guid projectId,
        Guid taskId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken);
        var attempt = (await _store.ListAttemptsAsync(taskId, cancellationToken))
                      .SingleOrDefault(candidate => candidate.Id == attemptId)
                      ?? throw new DevelopmentNotFoundException($"Development attempt '{attemptId}' was not found on the task.");
        if (attempt.Status is not (DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running))
        {
            return false;
        }

        return await _supervisor.TryCancelAsync(attemptId);
    }

    public async Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        _ = await _store.GetProjectAsync(projectId, cancellationToken);
        return await _store.ListEventsAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken);
        return await _store.ListArtifactsAsync(taskId, cancellationToken);
    }

    public async Task<DevelopmentArtifactContent> ReadArtifactAsync(Guid projectId,
        Guid taskId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken);
        var artifact = await _store.GetArtifactAsync(artifactId, cancellationToken);
        if (artifact.ProjectId != projectId || artifact.TaskId != taskId || artifact.ManagedReference is null)
        {
            throw new DevelopmentNotFoundException($"Development artifact '{artifactId}' was not found on the task.");
        }

        var read = await _blobStore.ReadAsync(projectId,
            artifact.Id,
            artifact.ContentHash,
            artifact.ByteCount,
            cancellationToken);
        if (read.Status != DevelopmentArtifactReadStatus.Found)
        {
            throw new DevelopmentInvalidTransitionException("The Development artifact failed immutable blob verification.");
        }

        return new DevelopmentArtifactContent { Artifact = artifact, Content = Encoding.UTF8.GetString(read.Content.Span) };
    }

    public async Task<DevelopmentPatchPreviewResult> PreviewAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken);
        var repository = await _repositoryBindings.ResolveProjectAsync(projectId, cancellationToken);
        var preview = await _applyService.PreviewAsync(taskId, repository, cancellationToken);
        return new DevelopmentPatchPreviewResult
        {
            SubjectHash = preview.Subject.SubjectHash,
            PatchHash = preview.Subject.PatchHash,
            ManifestHash = preview.Subject.ManifestHash,
            ExpectedResultHash = preview.Subject.ExpectedResultHash,
            Patch = preview.Patch,
            ChangedFiles = preview.ChangedFiles.Select(static file => new DevelopmentPatchPreviewFile { Path = file.Path, ChangeType = file.ChangeType, PreviousPath = file.PreviousPath }).ToArray()
        };
    }

    public async Task<DevelopmentOperationResult> ApplyAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
        Guid? onBehalfOfWorkflowRunId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken);
        await EnsureApplyAuthorityAsync(taskId, onBehalfOfWorkflowRunId, cancellationToken);
        var repository = await _repositoryBindings.ResolveProjectAsync(projectId, cancellationToken);
        return await _applyService.ApplyAsync(taskId, operationId, repository, cancellationToken);
    }

    /// <summary>
    ///     The apply gate: while the run driving a task is live, the approval that lets its patch land is a gate node
    ///     in that run, and this is not it.
    /// </summary>
    /// <remarks>
    ///     Both apply surfaces — the endpoint and the workflow's <c>DevWorkflowApplyCommands</c> — route through here,
    ///     because a hidden Apply button is a hint and any client could leave a HumanGate trail describing a decision
    ///     nobody made. The workflow lane passes by naming the run it applies for, so "on behalf of run X" against run
    ///     Y's task is refused. An ended run answers no further gate, so authority returns here; and with
    ///     <c>DevWorkflows:Enabled</c> off the guard stands down, or a run live at the flip strands its tasks for good.
    /// </remarks>
    private async Task EnsureApplyAuthorityAsync(Guid taskId, Guid? onBehalfOfWorkflowRunId, CancellationToken cancellationToken)
    {
        if (!_workflowOptions.Enabled)
        {
            return;
        }

        if (await _workflows.FindRunIdForDevelopmentTaskAsync(taskId, cancellationToken) is not { } runId
            || runId == onBehalfOfWorkflowRunId)
        {
            return;
        }

        DevWorkflowRunSnapshot run;
        try
        {
            run = await _workflows.GetRunAsync(runId, cancellationToken);
        }
        catch (DevWorkflowNotFoundException)
        {
            // The run was deleted between the two reads. There is no live owner left to defer to, and answering a 404
            // about a workflow to somebody applying a Development patch would be the wrong subject entirely.
            return;
        }

        if (!DevWorkflowStateMachine.IsTerminal(run.Status))
        {
            throw new DevelopmentInvalidTransitionException($"Development workflow run '{runId:D}' is driving this task and has not ended, so the "
                                                            + "approval that lets its patch land is that run's own gate. Approve it there, or wait "
                                                            + "for the run to end before applying from here.");
        }
    }

    public async Task<DevelopmentProjectAggregate> ReconnectRepositoryAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        _ = await _repositoryBindings.ReconnectAsync(projectId, selectedFolderId, expectedVersion, cancellationToken);
        return await GetProjectAsync(projectId, cancellationToken);
    }

    private async Task<DevelopmentTaskSnapshot> RequireTaskAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await _store.GetTaskAsync(taskId, cancellationToken);
        if (task.ProjectId != projectId)
        {
            throw new DevelopmentNotFoundException($"Development task '{taskId}' was not found on project '{projectId}'.");
        }

        return task;
    }

    private static Guid DerivedOperationId(Guid operationId, string phase)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(operationId.ToString("N"), ":", phase)));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
