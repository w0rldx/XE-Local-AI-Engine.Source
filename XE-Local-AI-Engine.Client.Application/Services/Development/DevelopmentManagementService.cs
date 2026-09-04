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

    /// <summary>
    ///     Puts a task's approved patch into the repository.
    ///     <para>
    ///         <paramref name="onBehalfOfWorkflowRunId" /> names the development-workflow run whose apply lane is
    ///         asking, and is the ONLY thing that gets an apply past a live run's ownership of that decision (Y3).
    ///         An operator surface — the endpoint — passes <see langword="null" />, which is what makes the refusal
    ///         server-side rather than a button a React build withholds.
    ///     </para>
    /// </summary>
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

internal sealed class DevelopmentManagementService(
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
    TimeProvider timeProvider) : IDevelopmentManagementService
{
    /// <summary>
    ///     Why a task with no rounds left was stood down — and, because it is PERSISTED as the task's reason and read
    ///     back to recognise that stand-down, also the sentinel for it. Says "rounds" rather than "review rounds"
    ///     because the budget stopped counting review entries alone: a failed deterministic gate spends one too.
    /// </summary>
    private const string ReviewRoundLimitReason = "The configured maximum number of rounds has been reached.";

    private readonly IDevelopmentApplyService _applyService = applyService ?? throw new ArgumentNullException(nameof(applyService));
    private readonly IDevelopmentArtifactBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly IDevelopmentCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IActiveCloudChatClientFactory _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
    private readonly IDevelopmentProfileBackfillService _profileBackfill = profileBackfill ?? throw new ArgumentNullException(nameof(profileBackfill));
    private readonly IDevelopmentCommandProfileDetector _profileDetector = profileDetector ?? throw new ArgumentNullException(nameof(profileDetector));
    private readonly IDevelopmentRepositoryBindingService _repositoryBindings = repositoryBindings ?? throw new ArgumentNullException(nameof(repositoryBindings));
    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDevelopmentAttemptExecutionSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
    private readonly IDevelopmentTemplateStore _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
    private readonly DevWorkflowOptions _workflowOptions = (workflowOptions ?? throw new ArgumentNullException(nameof(workflowOptions))).Value;

    /// <summary>
    ///     Read-only, and for one question: which workflow run — if any — owns the approval for a task.
    ///     <para>
    ///         Asked HERE rather than at the endpoint layer, which is the other place the answer could be composed:
    ///         this service is the ONE place a task aggregate is built — the project detail loops back through
    ///         <see cref="GetTaskAsync" /> for every task it carries — so composing it above would mean asking at three
    ///         call sites today and remembering to ask at the next one. Recorded so it is not re-litigated.
    ///     </para>
    /// </summary>
    private readonly IDevWorkflowStore _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public Task<DevelopmentRepositoryReference> RegisterRepositoryAsync(string displayAlias,
        string hostPath,
        CancellationToken cancellationToken = default) =>
        _repositoryBindings.RegisterAsync(displayAlias, hostPath, cancellationToken);

    public Task<IReadOnlyList<DevelopmentRepositoryReference>> ListRepositoriesAsync(CancellationToken cancellationToken = default) =>
        _repositoryBindings.ListAsync(cancellationToken);

    public async Task<DevelopmentProfileDetectionResult> DetectRepositoryProfileAsync(Guid selectedFolderId,
        CancellationToken cancellationToken = default)
    {
        var repository = await _repositoryBindings.ResolveFolderAsync(selectedFolderId, cancellationToken).ConfigureAwait(false);
        var detected = _profileDetector.Detect(repository.RepositoryRoot);
        return new DevelopmentProfileDetectionResult(detected.ProfileId, detected.BuildTarget, detected.Candidates);
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

        var repository = await _repositoryBindings.ResolveFolderAsync(input.SelectedFolderId, cancellationToken).ConfigureAwait(false);

        // The profile is snapshotted here, once, and is the only source of truth for the life of the project. It is
        // never re-read from the worktree during an attempt: the agent can write to the worktree, so a live read would
        // let it rewrite its own test command.
        // Template provenance is read from the materialization record rather than taken from the request: the client
        // must not be able to assert which template a repository came from.
        var materialization = await _templateStore.FindMaterializationAsync(input.SelectedFolderId, cancellationToken).ConfigureAwait(false);
        var profile = ResolveCommandProfile(input, repository.RepositoryRoot, materialization?.TemplateId.ToString());
        var projectId = DerivedOperationId(input.OperationId, "project");
        var taskId = DerivedOperationId(input.OperationId, "task");
        _ = await _coordinator.CreateProjectAsync(new DevelopmentCreateProjectCommand(projectId,
                                      taskId,
                                      input.OperationId,
                                      input.Objective,
                                      input.SelectedFolderId,
                                      repository.RepositoryIdentityHash,
                                      input.BaseBranch,
                                      input.TaskTitle,
                                      input.Requirements,
                                      input.AcceptanceCriteriaJson,
                                      input.EgressPolicy,
                                      input.CoderModelId,
                                      input.ReviewerModelId,
                                      TrustedRepositoryAcknowledged: true,
                                      TrustedRepositoryPolicyVersion: DevelopmentTrustPolicy.CurrentVersion,
                                      TrustedRepositoryAcknowledgedAtUtc: _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                                      MaxTokens: input.MaxTokens,
                                      MaxDurationSeconds: input.MaxDurationSeconds,
                                      CommandProfileJson: Encoding.UTF8.GetString(profile.ToCanonicalUtf8())),
                                  cancellationToken)
                              .ConfigureAwait(false);
        return await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
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

        // Precedence: the operator's explicit confirmation, then what the repository asked for, then detection. The
        // repository's request is only ever a choice among code-owned profiles — Materialize rejects anything else —
        // so a repository can select a profile but never define one.
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
        var project = await _store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        // Legacy projects predate the profile column. Filling it here, rather than only at startup, means a repository
        // that was offline at boot becomes usable as soon as it is back — no restart. A no-op once the profile exists.
        project = await _profileBackfill.EnsureAsync(project, cancellationToken).ConfigureAwait(false);
        var tasks = await _store.ListTasksAsync(projectId, cancellationToken).ConfigureAwait(false);

        // ONE query for every task's workflow pointer, not one per task: this read always has the whole task list, so
        // asking the single-task question in a loop is a round trip per row on the page that renders most often.
        var workflowRunIds = await _workflows.FindRunIdsForDevelopmentTasksAsync([.. tasks.Select(static task => task.Id)], cancellationToken)
                                             .ConfigureAwait(false);
        var aggregates = new List<DevelopmentTaskAggregate>(tasks.Count);
        foreach (var task in tasks)
        {
            aggregates.Add(new DevelopmentTaskAggregate(task,
                await _store.ListAttemptsAsync(task.Id, cancellationToken).ConfigureAwait(false),
                await _store.ListArtifactsAsync(task.Id, cancellationToken).ConfigureAwait(false),
                workflowRunIds.TryGetValue(task.Id, out var runId) ? runId : null));
        }

        return new DevelopmentProjectAggregate(project,
            aggregates,
            await _store.ListEventsAsync(projectId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<DevelopmentTaskAggregate> GetTaskAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);

        // Read back through the pointer a DevTask node run stamps rather than stored on the task: the task row belongs
        // to Development Mode, and a workflow driving one is a fact about the workflow.
        return new DevelopmentTaskAggregate(task,
            await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false),
            await _store.ListArtifactsAsync(taskId, cancellationToken).ConfigureAwait(false),
            await _workflows.FindRunIdForDevelopmentTaskAsync(taskId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<DevelopmentNextActionResult> StartNextActionAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        DevelopmentTrustPolicy.EnsureCurrent(project, _timeProvider);
        _ = await _repositoryBindings.ResolveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        var existing = await _store.FindOperationAsync(projectId,
            operationId,
            DevelopmentOperationPhases.Completed,
            cancellationToken).ConfigureAwait(false);
        if (existing?.AttemptId is { } existingAttemptId)
        {
            var existingAttempt = (await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false))
                .Single(attempt => attempt.Id == existingAttemptId);
            var existingTask = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
            return new DevelopmentNextActionResult("Attempt",
                projectId,
                taskId,
                existingAttemptId,
                existingTask.Status,
                existingAttempt.Role);
        }

        var task = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        if (task.Status == DevelopmentTaskStatus.Blocked
            && string.Equals(task.BlockedReason, ReviewRoundLimitReason, StringComparison.Ordinal))
        {
            return new DevelopmentNextActionResult("Blocked", projectId, taskId, null, DevelopmentTaskStatus.Blocked, null);
        }

        if (task.Status == DevelopmentTaskStatus.Planned)
        {
            var ready = await _coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                                  DerivedOperationId(operationId, "ready"),
                                                  DevelopmentTaskStatus.Ready,
                                                  task.Version),
                                              cancellationToken)
                                          .ConfigureAwait(false);
            task = (await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false)) with
            {
                Version = ready.Version
            };
        }

        var attempts = await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (attempts.Any(attempt => attempt.Status is DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running))
        {
            throw new DevelopmentInvalidTransitionException("The Development task already has an active attempt.");
        }

        // InProgress behind a SUCCEEDED coder attempt is the state that means "implemented, validate it".
        var awaitingValidation = task.Status == DevelopmentTaskStatus.InProgress
                                 && attempts.LastOrDefault(attempt => attempt.Role == DevelopmentAttemptRole.Coder) is
                                     { Status: DevelopmentAttemptStatus.Succeeded };

        // The budget is checked BEFORE the branch that would spend it, and covers the rework wait as well as the
        // validation wait: a task at the cap has nothing left whichever of the two it is sitting in. Gated on
        // ChangesRequested too because that is where a rejected review and a failed gate both leave it, and starting
        // the coder round from there first spent a whole model attempt — its tokens and its duration — on work that
        // could never reach a review. InReview is deliberately absent: that round is already paid for.
        if ((awaitingValidation || task.Status == DevelopmentTaskStatus.ChangesRequested)
            && task.CurrentReviewRound >= task.MaxReviewRounds)
        {
            _ = await _coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                          DerivedOperationId(operationId, "review-round-limit"),
                                          DevelopmentTaskStatus.Blocked,
                                          task.Version,
                                          ReviewRoundLimitReason),
                                      cancellationToken)
                                  .ConfigureAwait(false);
            return new DevelopmentNextActionResult("Blocked", projectId, taskId, null, DevelopmentTaskStatus.Blocked, null);
        }

        if (awaitingValidation)
        {
            if (!_supervisor.StartValidation(taskId))
            {
                throw new DevelopmentConcurrencyException("Deterministic validation is already scheduled for this task.");
            }

            return new DevelopmentNextActionResult("Validation", projectId, taskId, null, task.Status, null);
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

        // An ext: id is invisible to the cloud provider resolver by design (it falls through cloud selection), so its
        // locality is asked separately and refused under BOTH egress policies: the plan admits no dev-mode support for
        // a declared-cloud external model, and an UNRESOLVED one is refused with it because a deleted connection or an
        // unreadable store says nothing about where the prompt would go.
        if (ExternalModelId.HasExternalScheme(modelId)
            && await _modelTrustResolver.ResolveAsync(modelId, cancellationToken).ConfigureAwait(false) != ModelTrustLocality.Local)
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
        _ = await _coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(taskId,
                                      attemptId,
                                      operationId,
                                      role,
                                      modelId,
                                      provider,
                                      task.Version,
                                      predecessor),
                                  cancellationToken)
                              .ConfigureAwait(false);
        if (!_supervisor.StartAttempt(attemptId, role))
        {
            throw new DevelopmentConcurrencyException("The Development attempt is already scheduled.");
        }

        var startedTask = await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        return new DevelopmentNextActionResult("Attempt", projectId, taskId, attemptId, startedTask.Status, role);
    }

    public async Task<bool> CancelAttemptAsync(Guid projectId,
        Guid taskId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        var attempt = (await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false))
                      .SingleOrDefault(candidate => candidate.Id == attemptId)
                      ?? throw new KeyNotFoundException($"Development attempt '{attemptId}' was not found on the task.");
        if (attempt.Status is not (DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running))
        {
            return false;
        }

        return await _supervisor.TryCancelAsync(attemptId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        _ = await _store.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await _store.ListEventsAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        return await _store.ListArtifactsAsync(taskId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentArtifactContent> ReadArtifactAsync(Guid projectId,
        Guid taskId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        var artifact = await _store.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact.ProjectId != projectId || artifact.TaskId != taskId || artifact.ManagedReference is null)
        {
            throw new KeyNotFoundException($"Development artifact '{artifactId}' was not found on the task.");
        }

        var read = await _blobStore.ReadAsync(projectId,
            artifact.Id,
            artifact.ContentHash,
            artifact.ByteCount,
            cancellationToken).ConfigureAwait(false);
        if (read.Status != DevelopmentArtifactReadStatus.Found)
        {
            throw new DevelopmentInvalidTransitionException("The Development artifact failed immutable blob verification.");
        }

        return new DevelopmentArtifactContent(artifact, Encoding.UTF8.GetString(read.Content.Span));
    }

    public async Task<DevelopmentPatchPreviewResult> PreviewAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        var repository = await _repositoryBindings.ResolveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var preview = await _applyService.PreviewAsync(taskId, repository, cancellationToken).ConfigureAwait(false);
        return new DevelopmentPatchPreviewResult(preview.Subject.SubjectHash,
            preview.Subject.PatchHash,
            preview.Subject.ManifestHash,
            preview.Subject.ExpectedResultHash,
            preview.Patch,
            preview.ChangedFiles.Select(static file => new DevelopmentPatchPreviewFile(file.Path, file.ChangeType, file.PreviousPath)).ToArray());
    }

    public async Task<DevelopmentOperationResult> ApplyAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
        Guid? onBehalfOfWorkflowRunId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        await EnsureApplyAuthorityAsync(taskId, onBehalfOfWorkflowRunId, cancellationToken).ConfigureAwait(false);
        var repository = await _repositoryBindings.ResolveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await _applyService.ApplyAsync(taskId, operationId, repository, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Y3, enforced where it is actually enforceable: while the run driving a task is LIVE, the approval that lets
    ///     that task's patch land is a gate node in the run, and this gate is not it.
    ///     <para>
    ///         The Development page already hides its Apply button for such a task, but a hidden button is a hint, not
    ///         a rule — any client of this endpoint could apply the patch and leave the workflow's HumanGate trail
    ///         describing a decision nobody made. So the refusal lives here, in the one place both apply surfaces meet:
    ///         the endpoint and the workflow's own <c>DevWorkflowApplyCommands</c> both route through this method.
    ///     </para>
    ///     <para>
    ///         The workflow's lane gets through by NAMING the run it is applying for, rather than by an ambient flag: a
    ///         caller that says "on behalf of run X" and is refused because the task belongs to run Y is exactly the
    ///         case that should be refused. And once the run has ENDED it can answer no further gate, so the authority
    ///         returns here — withholding the apply then would strand an already-validated patch for good.
    ///     </para>
    ///     <para>
    ///         With the module SWITCHED OFF this guard stands down entirely, and has to: the dispatcher only runs when
    ///         <c>DevWorkflows:Enabled</c> is set, so a run that was live when the switch flipped never reaches a
    ///         terminal status and never answers another gate — an unconditional refusal would strand its tasks for
    ///         good, behind a workflow UI that is off too. Off means there is no competing gate to protect, which is
    ///         also the rule the client has always followed for the same reason.
    ///     </para>
    /// </summary>
    private async Task EnsureApplyAuthorityAsync(Guid taskId, Guid? onBehalfOfWorkflowRunId, CancellationToken cancellationToken)
    {
        if (!_workflowOptions.Enabled)
        {
            return;
        }

        if (await _workflows.FindRunIdForDevelopmentTaskAsync(taskId, cancellationToken).ConfigureAwait(false) is not { } runId
            || runId == onBehalfOfWorkflowRunId)
        {
            return;
        }

        DevWorkflowRunSnapshot run;
        try
        {
            run = await _workflows.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
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
        _ = await _repositoryBindings.ReconnectAsync(projectId, selectedFolderId, expectedVersion, cancellationToken).ConfigureAwait(false);
        return await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DevelopmentTaskSnapshot> RequireTaskAsync(Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task.ProjectId != projectId)
        {
            throw new KeyNotFoundException($"Development task '{taskId}' was not found on project '{projectId}'.");
        }

        return task;
    }

    private static Guid DerivedOperationId(Guid operationId, string phase)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(operationId.ToString("N"), ":", phase)));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
