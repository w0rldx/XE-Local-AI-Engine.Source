namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The work-session surface the REST layer sits on. Owns the rules that need more than one store to decide: which
///     agent a session may run on, when its objective may change, and what a follow-up does to a paused run.
/// </summary>
internal sealed class WorkSessionService : IWorkSessionService, IWorkflowOwnedWorkSessionLifecycle
{
    private const int MaxEventPageSize = 500;
    private const int MaxTitleLength = 200;
    private const int MaxObjectiveLength = 8000;

    private readonly IWorkSessionArtifactBlobStore _blobStore;
    private readonly IModelCapabilityResolver _capabilityResolver;
    private readonly KnowledgeBaseOptions _knowledgeOptions;
    private readonly ILogger<WorkSessionService> _logger;
    private readonly WorkSessionOptions _options;
    private readonly INodeChatPersistenceService _persistence;
    private readonly SecurityOptions _securityOptions;
    private readonly IAgentWorkSessionStore _store;
    private readonly IWorkSessionExecutionSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;
    private readonly WorkSessionToolGate _toolGate;

    public WorkSessionService(IAgentWorkSessionStore store,
        IWorkSessionArtifactBlobStore blobStore,
        INodeChatPersistenceService persistence,
        WorkSessionToolGate toolGate,
        IModelCapabilityResolver capabilityResolver,
        IWorkSessionExecutionSupervisor supervisor,
        IOptions<WorkSessionOptions> options,
        IOptions<SecurityOptions> securityOptions,
        IOptions<KnowledgeBaseOptions> knowledgeOptions,
        TimeProvider timeProvider,
        ILogger<WorkSessionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(securityOptions);
        ArgumentNullException.ThrowIfNull(knowledgeOptions);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _toolGate = toolGate ?? throw new ArgumentNullException(nameof(toolGate));
        _capabilityResolver = capabilityResolver ?? throw new ArgumentNullException(nameof(capabilityResolver));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _securityOptions = securityOptions.Value;
        _knowledgeOptions = knowledgeOptions.Value;
    }

    public async Task<IReadOnlyList<WorkSessionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. sessions.Select(static session => new WorkSessionSummary(session.Id,
                session.Title,
                session.Kind,
                session.Status,
                session.AgentDefinitionId,
                session.StepCount,
                session.UpdatedAtUtc))
        ];
    }

    public async Task<WorkSessionDetail> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ToDetail(await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false));

    public async Task<WorkSessionDetail> CreateAsync(CreateWorkSessionRequestModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        EnsureEnabled();

        var title = Require(model.Title, "title", MaxTitleLength);
        var objective = Require(model.Objective, "objective", MaxObjectiveLength);
        if (model.Kind == AgentWorkSessionKind.Development)
        {
            throw new WorkSessionValidationException("Development work sessions are not supported yet.");
        }

        _ = await ResolveToolCapableAgentAsync(model.AgentDefinitionId, model.Runtime?.ModelProfile, cancellationToken).ConfigureAwait(false);

        var conversation = await _persistence.CreateConversationAsync(new NodeChatCreateConversationRequest(title,
                                                     UserId: null,
                                                     _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                                                     NodeChatOriginValues.Local,
                                                     model.AgentDefinitionId,
                                                     NodeConversationKind.WorkSession),
                                                 cancellationToken)
                                             .ConfigureAwait(false);

        try
        {
            var created = await _store.CreateAsync(new CreateWorkSessionCommand(Guid.NewGuid(),
                                              conversation.ConversationId,
                                              model.AgentDefinitionId,
                                              model.Kind,
                                              title,
                                              objective),
                                          cancellationToken)
                                      .ConfigureAwait(false);
            return ToDetail(created);
        }
        catch
        {
            // The conversation exists only to carry this session. Leaving it behind would put an untitled empty chat in
            // the operator's list with nothing to explain it.
            await DeleteConversationAsync(conversation.ConversationId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<WorkSessionDetail> UpdateAsync(Guid sessionId, UpdateWorkSessionRequestModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var session = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var title = model.Title is null ? null : Require(model.Title, "title", MaxTitleLength);
        var objective = model.Objective is null ? null : Require(model.Objective, "objective", MaxObjectiveLength);

        if ((objective is not null || model.AgentDefinitionId is not null)
            && session.Status is not (AgentWorkSessionStatus.Draft or AgentWorkSessionStatus.Paused or AgentWorkSessionStatus.Interrupted))
        {
            throw new WorkSessionInvalidTransitionException($"A work session's objective and agent can only be changed while it is Draft, Paused or Interrupted; this one is {session.Status}.");
        }

        if (model.AgentDefinitionId is { } agentDefinitionId && agentDefinitionId != session.AgentDefinitionId)
        {
            var effectiveModel = await ResolveToolCapableAgentAsync(agentDefinitionId, pinnedModelOverride: null, cancellationToken).ConfigureAwait(false);
            await EnsureNoCloudEgressAsync(session, effectiveModel, cancellationToken).ConfigureAwait(false);
        }

        var updated = await _store.UpdateAsync(new UpdateWorkSessionCommand(sessionId, session.Version, title, objective, model.AgentDefinitionId), cancellationToken)
                                  .ConfigureAwait(false);
        return ToDetail(updated);
    }

    public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        DeleteAsync(sessionId, workflowOwned: false, cancellationToken);

    public Task<WorkSessionDetail> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        BeginAsync(sessionId, [AgentWorkSessionStatus.Draft], workflowOwned: false, runtime: null, cancellationToken);

    public Task<WorkSessionDetail> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        BeginAsync(sessionId, [AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Interrupted], workflowOwned: false, runtime: null, cancellationToken);

    public Task<WorkSessionDetail> PauseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        StopAsync(sessionId, WorkSessionStopReason.Pause, AgentWorkSessionStatus.Paused, "The operator paused the work session.", workflowOwned: false, cancellationToken);

    public Task<WorkSessionDetail> CancelAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        StopAsync(sessionId, WorkSessionStopReason.Cancel, AgentWorkSessionStatus.Cancelled, "The operator cancelled the work session.", workflowOwned: false, cancellationToken);

    bool IWorkflowOwnedWorkSessionLifecycle.HasCapacity => _supervisor.HasCapacity;

    Task<WorkSessionDetail> IWorkflowOwnedWorkSessionLifecycle.CreateAsync(string title,
        string objective,
        Guid agentDefinitionId,
        WorkSessionRuntimeOverride? runtime,
        CancellationToken cancellationToken) =>
        CreateAsync(new CreateWorkSessionRequestModel(title, objective, AgentWorkSessionKind.Workflow, agentDefinitionId, runtime), cancellationToken);

    Task<WorkSessionDetail> IWorkflowOwnedWorkSessionLifecycle.StartAsync(Guid sessionId, WorkSessionRuntimeOverride? runtime, CancellationToken cancellationToken) =>
        BeginAsync(sessionId, [AgentWorkSessionStatus.Draft], workflowOwned: true, runtime, cancellationToken);

    Task<WorkSessionDetail> IWorkflowOwnedWorkSessionLifecycle.ResumeAsync(Guid sessionId, WorkSessionRuntimeOverride? runtime, CancellationToken cancellationToken) =>
        BeginAsync(sessionId, [AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Interrupted], workflowOwned: true, runtime, cancellationToken);

    Task<WorkSessionDetail> IWorkflowOwnedWorkSessionLifecycle.PauseAsync(Guid sessionId, CancellationToken cancellationToken) =>
        StopAsync(sessionId, WorkSessionStopReason.Pause, AgentWorkSessionStatus.Paused, "The workflow run paused the work session.", workflowOwned: true, cancellationToken);

    Task<WorkSessionDetail> IWorkflowOwnedWorkSessionLifecycle.CancelAsync(Guid sessionId, CancellationToken cancellationToken) =>
        StopAsync(sessionId, WorkSessionStopReason.Cancel, AgentWorkSessionStatus.Cancelled, "The workflow run cancelled the work session.", workflowOwned: true, cancellationToken);

    Task IWorkflowOwnedWorkSessionLifecycle.DeleteAsync(Guid sessionId, CancellationToken cancellationToken) =>
        DeleteAsync(sessionId, workflowOwned: true, cancellationToken);

    private async Task DeleteAsync(Guid sessionId, bool workflowOwned, CancellationToken cancellationToken)
    {
        var session = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureCallerOwns(session, workflowOwned);
        if (session.Status is AgentWorkSessionStatus.Running or AgentWorkSessionStatus.WaitingForApproval or AgentWorkSessionStatus.WaitingForInput)
        {
            throw new WorkSessionInvalidTransitionException("Cancel the work session before deleting it; a step is still running.");
        }

        _ = await _store.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // The rows go first, then the bytes and the conversation. The store cannot reach either — the schema project
        // does not depend on the application layer — so sweeping them is this service's job, not the store's.
        _blobStore.DeleteSession(sessionId);
        await DeleteConversationAsync(session.ConversationId).ConfigureAwait(false);
    }

    public async Task<Guid> PostFollowUpAsync(Guid sessionId, string text, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var session = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new WorkSessionValidationException("A follow-up needs some text.");
        }

        // The node's message-size cap is checked in the chat hub, which a REST follow-up never passes through — and the
        // row is persisted before anything downstream could inspect it. Checked here, before the write, so an over-cap
        // follow-up persists nothing.
        var sizeBytes = Encoding.UTF8.GetByteCount(text);
        var maxBytes = _securityOptions.MaxMessageSizeKb * 1024;
        if (sizeBytes > maxBytes)
        {
            var sizeKb = (sizeBytes + 1023) / 1024;
            throw new WorkSessionValidationException(string.Create(CultureInfo.InvariantCulture,
                $"That follow-up is too large ({sizeKb} KB, limit {_securityOptions.MaxMessageSizeKb} KB)."));
        }

        var messageId = Guid.NewGuid();
        _ = await _persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(session.ConversationId,
                                      messageId,
                                      text.Trim(),
                                      _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
                                  cancellationToken)
                              .ConfigureAwait(false);

        // A paused or interrupted session picks the follow-up up by resuming: it rides the next step's history like any
        // other user turn. A parked one does not — its live step already holds the node's invocation slot, and its
        // prompt is answered through the chat card, not here. A workflow-owned one does not either: the refusal below
        // lands in the same catch, and the run resumes the session on its next poll with the message already in place.
        if (session.Status is AgentWorkSessionStatus.Paused or AgentWorkSessionStatus.Interrupted && _supervisor.HasCapacity)
        {
            try
            {
                _ = await BeginAsync(sessionId,
                        [AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Interrupted],
                        workflowOwned: false,
                        runtime: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkSessionInvalidTransitionException exception)
            {
                // The session moved between the read and the resume. The follow-up is persisted either way, so this is
                // a missed convenience, not a failure the caller needs to see.
                _logger.LogDebug(exception, "Work session {SessionId} could not auto-resume after a follow-up.", sessionId);
            }
        }

        return messageId;
    }

    public async Task<IReadOnlyList<WorkSessionTaskDto>> ListTasksAsync(Guid sessionId, long sinceSequence, CancellationToken cancellationToken = default)
    {
        var tasks = await _store.ListTasksAsync(sessionId, sinceSequence, cancellationToken).ConfigureAwait(false);
        return
        [
            .. tasks.Select(static task => new WorkSessionTaskDto(task.Id,
                task.ParentTaskId,
                task.Sequence,
                task.Title,
                task.Detail,
                task.Status,
                task.BlockedReason,
                task.Origin,
                task.CreatedStep,
                task.UpdatedStep))
        ];
    }

    public async Task<IReadOnlyList<WorkSessionFindingDto>> ListFindingsAsync(Guid sessionId, long sinceSequence, CancellationToken cancellationToken = default)
    {
        var findings = await _store.ListFindingsAsync(sessionId, sinceSequence, cancellationToken).ConfigureAwait(false);
        return
        [
            .. findings.Select(static finding => new WorkSessionFindingDto(finding.Id,
                finding.TaskId,
                finding.Sequence,
                finding.Kind,
                finding.Text,
                finding.SourceRef,
                finding.CreatedStep,
                finding.Superseded))
        ];
    }

    public async Task<IReadOnlyList<WorkSessionArtifactDto>> ListArtifactsAsync(Guid sessionId, long sinceSequence, CancellationToken cancellationToken = default)
    {
        var artifacts = await _store.ListArtifactsAsync(sessionId, sinceSequence, cancellationToken).ConfigureAwait(false);
        return [.. artifacts.Select(ToDto)];
    }

    public async Task<IReadOnlyList<WorkSessionCheckpointDto>> ListCheckpointsAsync(Guid sessionId, long sinceSequence, CancellationToken cancellationToken = default)
    {
        var checkpoints = await _store.ListCheckpointsAsync(sessionId, sinceSequence, cancellationToken).ConfigureAwait(false);
        return
        [
            .. checkpoints.Select(static checkpoint => new WorkSessionCheckpointDto(checkpoint.Id,
                checkpoint.Sequence,
                checkpoint.Step,
                checkpoint.Summary,
                checkpoint.StateJson,
                checkpoint.CreatedAtUtc))
        ];
    }

    public async Task<IReadOnlyList<WorkSessionEventDto>> ListEventsAsync(Guid sessionId, long sinceSequence, int limit, CancellationToken cancellationToken = default)
    {
        var events = await _store.ListEventsAsync(sessionId, sinceSequence, cancellationToken).ConfigureAwait(false);
        var clamped = limit <= 0 ? MaxEventPageSize : Math.Min(limit, MaxEventPageSize);
        return
        [
            .. events.Take(clamped)
                     .Select(static entry => new WorkSessionEventDto(entry.Id,
                         entry.Sequence,
                         entry.Step,
                         entry.EventType,
                         entry.DetailJson,
                         entry.Outcome,
                         entry.OccurredAtUtc,
                         entry.OperationId))
        ];
    }

    public async Task<WorkSessionArtifactDto> GetArtifactAsync(Guid sessionId, Guid artifactId, CancellationToken cancellationToken = default)
    {
        return ToDto(await GetOwnedArtifactAsync(sessionId, artifactId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<WorkSessionArtifactContent> ReadArtifactContentAsync(Guid sessionId, Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await GetOwnedArtifactAsync(sessionId, artifactId, cancellationToken).ConfigureAwait(false);

        var read = await _blobStore.ReadAsync(sessionId, artifactId, artifact.ContentSha256, artifact.SizeBytes, cancellationToken).ConfigureAwait(false);
        if (read.Status != WorkSessionArtifactReadStatus.Found)
        {
            // A tampered or missing blob is not content the node can vouch for, so it is not content the node hands
            // over. The row stays, and the read reads as "gone".
            _logger.LogWarning("Work session artifact {ArtifactId} could not be read: {Status}.", artifactId, read.Status);
            throw new WorkSessionNotFoundException($"Work session artifact '{artifactId}' could not be read.");
        }

        var isBase64 = !ArtifactMediaTypes.IsText(artifact.MediaType);
        var content = isBase64 ? Convert.ToBase64String(read.Content.Span) : Encoding.UTF8.GetString(read.Content.Span);
        return new WorkSessionArtifactContent(ToDto(artifact), content, isBase64);
    }

    /// <summary>
    ///     The artifact row, once. An artifact that belongs to another session — or whose bytes the node already marked
    ///     invalid — reads as absent, so neither reader can leak one session's artifact through another's route.
    /// </summary>
    private async Task<WorkSessionArtifactSnapshot> GetOwnedArtifactAsync(Guid sessionId, Guid artifactId, CancellationToken cancellationToken)
    {
        var artifact = await _store.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact.SessionId != sessionId || !artifact.IsValid)
        {
            throw new WorkSessionNotFoundException($"Work session artifact '{artifactId}' was not found on session '{sessionId}'.");
        }

        return artifact;
    }

    private async Task<WorkSessionDetail> BeginAsync(Guid sessionId,
        AgentWorkSessionStatus[] allowedFrom,
        bool workflowOwned,
        WorkSessionRuntimeOverride? runtime,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();

        var session = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureCallerOwns(session, workflowOwned);
        if (!allowedFrom.Contains(session.Status))
        {
            throw new WorkSessionInvalidTransitionException($"A work session in {session.Status} cannot be started from here.");
        }

        // Capacity is read BEFORE the status moves. Moving first and failing admission afterwards would leave a session
        // reading Running with nothing driving it.
        if (!_supervisor.HasCapacity)
        {
            throw new WorkSessionInvalidTransitionException("The node is already running as many work sessions as it allows. Pause one first.");
        }

        var running = await _store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, session.Version, AgentWorkSessionStatus.Running), cancellationToken)
                                  .ConfigureAwait(false);
        if (_supervisor.TryStart(sessionId, runtime))
        {
            return ToDetail(running);
        }

        var parked = await _store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId,
                                         WorkSessionVersions.Any,
                                         AgentWorkSessionStatus.Paused,
                                         CurrentTaskId: null,
                                         "The node could not admit the work session."),
                                     cancellationToken)
                                 .ConfigureAwait(false);
        _logger.LogWarning("Work session {SessionId} lost the admission race and was left Paused.", sessionId);
        _ = parked;
        throw new WorkSessionInvalidTransitionException("The node could not admit the work session just now. Try again in a moment.");
    }

    private async Task<WorkSessionDetail> StopAsync(Guid sessionId,
        WorkSessionStopReason reason,
        AgentWorkSessionStatus target,
        string sanitizedReason,
        bool workflowOwned,
        CancellationToken cancellationToken)
    {
        var session = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureCallerOwns(session, workflowOwned);
        if (await _supervisor.TryStopAsync(sessionId, reason, cancellationToken).ConfigureAwait(false))
        {
            // The loop owns the terminal write, including the checkpoint that has to precede it. Re-read rather than
            // asserting a status this method did not write.
            return ToDetail(await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false));
        }

        var settled = await _store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, session.Version, target, CurrentTaskId: null, sanitizedReason),
                                      cancellationToken)
                                  .ConfigureAwait(false);
        return ToDetail(settled);
    }

    /// <summary>
    ///     Resolves the agent and answers with the model the session would actually run on. A session bound to a model
    ///     that cannot call tools would burn its whole step budget writing nothing, so it is refused at the boundary
    ///     rather than discovered on step 25.
    ///     <para>
    ///         BOTH tool gates are checked, and their refusals are worded differently because their fixes are: a model
    ///         whose template cannot call tools needs a different agent, while a model the operator has not listed needs
    ///         one line in Node Settings. Checking only the capability probe is what made this silent — the offer
    ///         applies the allow-list too, so create succeeded and every state-tool call then came back "Requested
    ///         function … not found".
    ///     </para>
    /// </summary>
    private async Task<string?> ResolveToolCapableAgentAsync(Guid agentDefinitionId, string? pinnedModelOverride, CancellationToken cancellationToken)
    {
        var verdict = await _toolGate.InspectAsync(agentDefinitionId, pinnedModelOverride, cancellationToken).ConfigureAwait(false);
        if (!verdict.AgentExists)
        {
            throw new WorkSessionValidationException("That agent could not be found. It may have been deleted.");
        }

        if (verdict.EffectiveModel is null)
        {
            throw new WorkSessionValidationException("This node has no chat model a work session could run on. Install one, or pin a model on the agent.");
        }

        if (verdict.SupportsTools is false)
        {
            throw new WorkSessionValidationException(
                $"{verdict.Subject}, which cannot call tools, so it could never record a task or a finding. Use a tool-capable model.");
        }

        if (!verdict.IsAllowListed)
        {
            throw new WorkSessionValidationException(WorkSessionToolGate.AllowListRefusal(verdict));
        }

        return verdict.EffectiveModel;
    }

    /// <summary>
    ///     Refuses repointing a session that already holds findings at a cloud-effective agent.
    ///     <para>
    ///         The knowledge-base cloud gate is per turn and acts on the OFFER: it withholds the local-data tools from a
    ///         cloud model. It says nothing about text a local model already extracted. Without this check, research on a
    ///         local model, then a pause, a repoint at a cloud agent and a resume would hand the whole findings corpus to
    ///         a third-party provider inside the next step's state block.
    ///     </para>
    /// </summary>
    private async Task EnsureNoCloudEgressAsync(AgentWorkSessionSnapshot session, string? effectiveModel, CancellationToken cancellationToken)
    {
        if (_knowledgeOptions.AllowCloudModelAccess)
        {
            return;
        }

        var capabilities = await _capabilityResolver.ResolveAsync(effectiveModel, cancellationToken).ConfigureAwait(false);
        if (!capabilities.IsCloud)
        {
            return;
        }

        var findings = await _store.ListFindingsAsync(session.Id, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        if (findings.Count > 0)
        {
            throw new WorkSessionValidationException("This work session already holds findings taken on a node-local model. Moving it to a cloud model would send them off the node; "
                                                     + "start a new session, or enable KnowledgeBase:AllowCloudModelAccess if that is what you want.");
        }
    }

    private async Task DeleteConversationAsync(Guid conversationId)
    {
        try
        {
            _ = await _persistence.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversationId,
                                          _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                                          PurgeImmediately: true),
                                      CancellationToken.None)
                                  .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _logger.LogWarning(exception, "Could not delete the conversation {ConversationId} a work session owned.", conversationId);
        }
    }

    /// <summary>
    ///     Keeps the two lifecycle surfaces from crossing. A workflow run owns its sessions outright, so a lifecycle call
    ///     arriving through <see cref="IWorkSessionService" /> — the REST layer, the Work Sessions page, any headless
    ///     caller — is refused, and the operator's control is pausing the run instead. The mirror case is refused for the
    ///     same reason: <see cref="IWorkflowOwnedWorkSessionLifecycle" /> must not reach a session no run is driving.
    /// </summary>
    private static void EnsureCallerOwns(AgentWorkSessionSnapshot session, bool workflowOwned)
    {
        var sessionIsWorkflowOwned = session.Kind == AgentWorkSessionKind.Workflow;
        if (sessionIsWorkflowOwned == workflowOwned)
        {
            return;
        }

        throw new WorkSessionInvalidTransitionException(sessionIsWorkflowOwned
            ? "This work session belongs to a development workflow run; pause, resume or cancel the run instead."
            : "This work session belongs to no development workflow run, so a run cannot drive its lifecycle.");
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new WorkSessionValidationException("Work sessions are disabled on this node.");
        }
    }

    private WorkSessionDetail ToDetail(AgentWorkSessionSnapshot session) =>
        new(session.Id,
            session.Title,
            session.Objective,
            session.Kind,
            session.Status,
            session.AgentDefinitionId,
            session.ConversationId,
            session.CurrentTaskId,
            session.StepCount,
            _options.MaxStepsPerRun,
            session.LastCheckpointId,
            session.LastSequence,
            session.Version,
            session.CreatedAtUtc,
            session.UpdatedAtUtc);

    private static WorkSessionArtifactDto ToDto(WorkSessionArtifactSnapshot artifact) =>
        new(artifact.Id,
            artifact.Sequence,
            artifact.Kind,
            artifact.Name,
            artifact.MediaType,
            artifact.ContentSha256,
            artifact.SizeBytes,
            artifact.IsValid,
            artifact.CreatedStep);

    private static string Require(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkSessionValidationException($"A work session needs a {field}.");
        }

        var trimmed = value.Trim();
        return trimmed.Length > maximumLength
            ? throw new WorkSessionValidationException($"The {field} is longer than the {maximumLength}-character limit.")
            : trimmed;
    }
}
