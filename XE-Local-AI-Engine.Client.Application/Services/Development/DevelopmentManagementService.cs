namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
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

    Task<DevelopmentOperationResult> ApplyAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
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
    TimeProvider timeProvider) : IDevelopmentManagementService
{
    private const string ReviewRoundLimitReason = "The configured maximum review rounds has been reached.";

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
        var aggregates = new List<DevelopmentTaskAggregate>(tasks.Count);
        foreach (var task in tasks)
        {
            aggregates.Add(await GetTaskAsync(projectId, task.Id, cancellationToken).ConfigureAwait(false));
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
        return new DevelopmentTaskAggregate(task,
            await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false),
            await _store.ListArtifactsAsync(taskId, cancellationToken).ConfigureAwait(false));
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

        if (task.Status == DevelopmentTaskStatus.InProgress
            && attempts.LastOrDefault(attempt => attempt.Role == DevelopmentAttemptRole.Coder) is { Status: DevelopmentAttemptStatus.Succeeded })
        {
            if (task.CurrentReviewRound >= task.MaxReviewRounds)
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
            throw new DevelopmentWorkspaceSecurityException(
                "Development execution cannot start with an external model that is not declared local to this node's trust boundary.");
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
        CancellationToken cancellationToken = default)
    {
        _ = await RequireTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        var repository = await _repositoryBindings.ResolveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await _applyService.ApplyAsync(taskId, operationId, repository, cancellationToken).ConfigureAwait(false);
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
