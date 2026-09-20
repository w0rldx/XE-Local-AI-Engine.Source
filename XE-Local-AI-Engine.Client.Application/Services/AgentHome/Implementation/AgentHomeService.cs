namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     AgentHome gateway <see cref="IAgentHomeService" />, driving the orchestration end-to-end against the
///     configured provider (the deterministic fake by default).
/// </summary>
/// <remarks>
///     <see cref="RunLifecycleAsync" /> resolves owner/node identity once, takes the shared exclusive lease keyed by that owner-node,
///     then runs Prepare + Run under it. This class owns identity, the lease, the workspace copy and the patch and depends on no
///     model, while <see cref="IAgentHomeGoalExecutor" /> owns the inner agent, its sandbox-scoped tools and the whole-run budgets.
///     Cancellation is classified with the work: a caller cancel propagates from the executor and unwinds the lease here, while a
///     budget cut-off returns a non-throwing outcome, so a partial run still exports its patch. See <c>docs/wiki/04-agent-mode.md</c> §2.2.
/// </remarks>
internal sealed class AgentHomeService : IAgentHomeService, IConversationSandboxStager
{
    // Stable sandbox alias for staged conversation upload attachments. The agent reads them at
    // workspace/selected/attachments/ via its existing file tools.
    private const string AttachmentsFolderAlias = "attachments";

    private static readonly AgentHomePatchExport EmptyPatchExport = new()
    {
        ChangedFileCount = 0,
        Blocked = false,
        PatchBytes = 0,
        PatchRelativePath = null,
        ChangedFilesRelativePath = null
    };

    private readonly ComputeOptions _ceilingDefaults;
    private readonly IAgentHomeGoalExecutor _goalExecutor;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly IAgentHomeExecutionLeaseManager _leaseManager;
    private readonly IAgentHomeWorkspaceIsolation _isolation;
    private readonly ILogger<AgentHomeService> _logger;
    private readonly IAgentHomeManifestService _manifestService;
    private readonly AgentHomeOptions _options;
    private readonly IAgentHomePatchService _patchService;
    private readonly LocalContainerOptions _nodeOptions;
    private readonly IAgentSandboxRuntimeProvider _provider;
    private readonly SandboxOptions _sandboxOptions;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IConversationUploadedFileStore _uploadedFileStore;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentHomeWorkspaceService _workspaceService;
    private int _runCounter;

    public AgentHomeService(IAgentHomeManifestService manifestService,
        IAgentSandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        IAgentHomeExecutionLeaseManager leaseManager,
        IAgentHomeWorkspaceIsolation isolation,
        IAgentHomeWorkspaceService workspaceService,
        IAgentHomePatchService patchService,
        IAgentHomeGoalExecutor goalExecutor,
        IServiceScopeFactory scopeFactory,
        IOptions<AgentHomeOptions> options,
        IOptions<SandboxOptions> sandboxOptions,
        IOptions<ComputeOptions> ceilingDefaults,
        IOptions<LocalContainerOptions> nodeOptions,
        INodeRuntimeSettings runtimeSettings,
        IConversationUploadedFileStore uploadedFileStore,
        TimeProvider timeProvider,
        ILogger<AgentHomeService> logger)
    {
        _manifestService = manifestService ?? throw new ArgumentNullException(nameof(manifestService));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
        _isolation = isolation ?? throw new ArgumentNullException(nameof(isolation));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _patchService = patchService ?? throw new ArgumentNullException(nameof(patchService));
        _goalExecutor = goalExecutor ?? throw new ArgumentNullException(nameof(goalExecutor));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ArgumentNullException.ThrowIfNull(sandboxOptions);
        _sandboxOptions = sandboxOptions.Value;
        ArgumentNullException.ThrowIfNull(ceilingDefaults);
        _ceilingDefaults = ceilingDefaults.Value;
        ArgumentNullException.ThrowIfNull(nodeOptions);
        _nodeOptions = nodeOptions.Value;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _uploadedFileStore = uploadedFileStore ?? throw new ArgumentNullException(nameof(uploadedFileStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentHomeRunResult> RunLifecycleAsync(AgentHomeRunLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Resolve identity first so the lease key exists before Prepare. A second run for the same owner-node while one
        // is in flight is rejected, not queued.
        var identity = await _identityProvider.GetAsync(cancellationToken);
        var key = LeaseKey(identity);
        if (_leaseManager.IsPoisoned(key))
        {
            throw new AgentHomeRequestRejectedException("the AgentHome workspace is unavailable until isolation recovery succeeds.");
        }

        using var lease = _leaseManager.TryAcquire(key);
        if (lease is null)
        {
            throw new AgentHomeBusyException("an AgentHome run is already in progress for this node.");
        }

        var prepareRequest = new AgentHomePrepareRequest
        {
            SelectedFolderIds = request.SelectedFolderIds,
            RuntimeProfile = request.RuntimeProfile,
            ConversationId = request.ConversationId
        };
        var effectiveProfile = _options.DefaultRuntimeProfile;
        var attachKey = CreateAttachKey(identity, effectiveProfile);

        try
        {
            effectiveProfile = ResolveRuntimeProfile(prepareRequest.RuntimeProfile);
            attachKey = CreateAttachKey(identity, effectiveProfile);
            var prepared = await PrepareUnderLeaseAsync(prepareRequest, attachKey, effectiveProfile, cancellationToken);
            var result = await RunAsync(new AgentHomeRunRequest
                {
                    Prepared = prepared,
                    Goal = request.Goal,
                    AllowedActions = request.AllowedActions
                },
                cancellationToken);
            if (!result.Completed || result.TimedOut)
            {
                _ = await _isolation.ClearAsync(prepared.Handle, key, CancellationToken.None);
            }

            return result;
        }
        catch
        {
            await RecoverAfterFailureAsync(attachKey, key);
            throw;
        }
    }

    private async Task<AgentHomePrepareResult> PrepareUnderLeaseAsync(AgentHomePrepareRequest request,
        SandboxAttachKey attachKey,
        string effectiveProfile,
        CancellationToken cancellationToken)
    {
        var prepareTimeoutSeconds = await _runtimeSettings.GetAgentHomePrepareTimeoutSecondsAsync(cancellationToken);
        using var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prepareCts.CancelAfter(TimeSpan.FromSeconds(prepareTimeoutSeconds));
        var prepareToken = prepareCts.Token;

        var layout = await _manifestService.InitializeAsync(attachKey, prepareToken);

        var createRequest = new SandboxCreateRequest
        {
            AttachKey = attachKey,
            RuntimeProfile = effectiveProfile,
            // Ask for a real filesystem boundary wherever the backend advertises one, one posture for every run: the request
            // is capability-gated so it can never fail the run closed, and CreateOrAttach reuses an owner-node sandbox.
            Isolation = _provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation)
                ? SandboxIsolationMode.Filesystem
                : SandboxIsolationMode.None,

            // Default-deny egress wherever the provider can enforce it: everything AgentHome and Coder run in the sandbox
            // is local, so denial costs no capability. Capability-gated, with RequireEgressDenial demanding a refusal.
            NetworkPolicy = SandboxEgressPolicy.Resolve(_provider.Capabilities,
                _sandboxOptions.RequireEgressDenial,
                SandboxEgressPolicy.AgentOptionKey,
                SandboxWorkloads.AgentHome.Workload),

            // The node's ceilings wherever the backend can impose them, derived through the one helper every create
            // site shares so this request cannot disagree with SandboxWorkloads.AgentHome's declaration.
            ResourceLimits = SandboxResourceCeilings.Resolve(SandboxWorkloads.AgentHome, _provider.Capabilities, _ceilingDefaults, _nodeOptions)
        };
        var handle = await _provider.CreateOrAttachAsync(createRequest, prepareToken);

        // Clear before resolution so preparation never reasons over a prior selection. The workspace service resets
        // again immediately before copying; the lifecycle catch performs final recovery on every failure.
        await _workspaceService.PrepareSelectedFoldersAsync(handle, [], prepareToken);
        return await PrepareAttachedAsync(request, effectiveProfile, attachKey, layout, handle, prepareToken);
    }

    private async Task<AgentHomePrepareResult> PrepareAttachedAsync(AgentHomePrepareRequest request,
        string effectiveProfile,
        SandboxAttachKey attachKey,
        AgentHomeLayout layout,
        SandboxHandle handle,
        CancellationToken prepareToken)
    {
        var resolvedFolders = await ResolveFoldersAsync(request.SelectedFolderIds, prepareToken);

        // Stage the conversation's extracted, decrypted attachments as a synthetic read-only "attachments" folder the
        // file tools discover. The snapshot holds plaintext: the finally disposes it the moment the copy completes.
        var foldersToCopy = resolvedFolders;
        IReadOnlyList<string> stagedAttachmentPaths = [];
        IConversationStagingSnapshot? attachmentsSnapshot = null;
        IReadOnlyList<SelectedFolderSnapshot> folderSnapshots;
        try
        {
            attachmentsSnapshot = await TryStageConversationAttachmentsAsync(request.ConversationId, prepareToken);
            if (attachmentsSnapshot is not null)
            {
                foldersToCopy =
                [
                    .. resolvedFolders,
                    new ResolvedSelectedFolder { Id = Guid.NewGuid(), Alias = AttachmentsFolderAlias, HostPath = attachmentsSnapshot.HostPath, Mode = SelectedFolderMode.Copy }
                ];

                // Capture the workspace-relative staged paths before the snapshot is disposed, so the chat agent-mode
                // path can point the model straight at them (e.g. attachments/report.md).
                stagedAttachmentPaths =
                [
                    .. attachmentsSnapshot.FileNames.Select(name => string.Create(CultureInfo.InvariantCulture, $"{AttachmentsFolderAlias}/{name}"))
                ];
            }

            // Workspace copy: each resolved selected folder into the sandbox workspace, with exclusions, the symlink-escape
            // guard, the per-folder byte budget and the git baseline. Under the preparation timeout, not the command one.
            folderSnapshots = await _workspaceService
                                    .PrepareSelectedFoldersAsync(handle, foldersToCopy, prepareToken);
        }
        finally
        {
            if (attachmentsSnapshot is not null)
            {
                await attachmentsSnapshot.DisposeAsync();
            }
        }

        _logger.LogInformation("AgentHome prepared for node {NodeId}: sandbox {SandboxId}, {FolderCount} selected folder(s) resolved.",
            attachKey.NodeId,
            handle.SandboxId,
            foldersToCopy.Count);

        return new AgentHomePrepareResult
        {
            Layout = layout,
            Handle = handle,
            ResolvedFolders = foldersToCopy,
            FolderSnapshots = folderSnapshots,
            RuntimeProfile = effectiveProfile,
            StagedAttachmentRelativePaths = stagedAttachmentPaths
        };
    }

    public async Task<ConversationSandboxPreparation> PrepareConversationAttachmentsAsync(Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Agent Mode off → the coder / run_in_agent_home tool handlers refuse at execution anyway, so skip the prepare
        // entirely rather than create a sandbox that nothing can read.
        if (!_options.Enabled)
        {
            return new ConversationSandboxPreparation([], lease: null);
        }

        var identity = await _identityProvider.GetAsync(cancellationToken);
        var key = LeaseKey(identity);
        if (_leaseManager.IsPoisoned(key))
        {
            return new ConversationSandboxPreparation([], lease: null, isBusy: true);
        }

        var lease = _leaseManager.TryAcquire(key);

        // Share the owner-node execution lease with RunLifecycleAsync so an in-flight run and a chat-mode re-stage cannot
        // race on one sandbox. Non-blocking: a held lease skips the re-stage rather than blocking the chat turn.
        if (lease is null)
        {
            _logger.LogDebug("AgentHome attachment staging for node {NodeId} skipped: a run is already in progress.", identity.NodeId);
            return new ConversationSandboxPreparation([], lease: null, isBusy: true);
        }

        try
        {
            // PrepareSelectedFoldersAsync unconditionally replaces the selected root, so this staging leaves only the
            // current conversation's attachments without tearing down the owner-node sandbox.
            var effectiveProfile = ResolveRuntimeProfile(requestedProfile: null);
            var attachKey = CreateAttachKey(identity, effectiveProfile);
            var prepared = await PrepareUnderLeaseAsync(new AgentHomePrepareRequest
            {
                SelectedFolderIds = [],
                RuntimeProfile = null,
                ConversationId = conversationId
            }, attachKey, effectiveProfile, cancellationToken);
            return new ConversationSandboxPreparation(prepared.StagedAttachmentRelativePaths, lease);
        }
        catch
        {
            await RecoverAfterFailureAsync(CreateAttachKey(identity, ResolveRuntimeProfile(requestedProfile: null)), key);
            lease.Dispose();
            throw;
        }
    }

    private static AgentHomeExecutionLeaseKey LeaseKey(AgentHomeOwnerIdentity identity)
    {
        return new AgentHomeExecutionLeaseKey(identity.OwnerUserId, identity.NodeId);
    }

    private SandboxAttachKey CreateAttachKey(AgentHomeOwnerIdentity identity, string effectiveProfile)
    {
        return new SandboxAttachKey
        {
            OwnerUserId = identity.OwnerUserId,
            NodeId = identity.NodeId,
            ProviderName = _provider.ProviderName,
            RuntimeProfile = effectiveProfile,
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
    }

    private async Task RecoverAfterFailureAsync(SandboxAttachKey attachKey, AgentHomeExecutionLeaseKey key)
    {
        try
        {
            await _isolation.RecoverExistingAsync(attachKey, key, CancellationToken.None);
        }
        catch (AgentHomeWorkspacePoisonedException exception)
        {
            _logger.LogError(exception, "AgentHome failure cleanup could not prove workspace isolation for node {NodeId}.", attachKey.NodeId);
        }
    }

    // Builds a decrypted-Markdown staging snapshot for the conversation's attachments, or null without a conversation or
    // extracted files. The caller appends its host path as a folder and disposes it. Logs counts and aliases only.
    private async Task<IConversationStagingSnapshot?> TryStageConversationAttachmentsAsync(Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId is not { } resolvedConversationId)
        {
            return null;
        }

        var files = await _uploadedFileStore.ListAsync(resolvedConversationId, cancellationToken);
        if (files.Count == 0)
        {
            return null;
        }

        var snapshot = await _uploadedFileStore.CreateStagingSnapshotAsync(resolvedConversationId, cancellationToken);
        if (snapshot.FileCount == 0)
        {
            // No extracted Markdown was cached (e.g. every file was unsupported/failed): nothing to stage. Dispose the
            // empty temp dir rather than leave it to the caller.
            await snapshot.DisposeAsync();
            return null;
        }

        _logger.LogInformation("AgentHome staging {FileCount} conversation attachment(s) under alias '{Alias}'.",
            snapshot.FileCount,
            AttachmentsFolderAlias);
        return snapshot;
    }

    private async Task<AgentHomeRunResult> RunAsync(AgentHomeRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var runId = CreateRunId();

        // Re-check cancellation before touching the host filesystem so an early cancel leaves no orphaned run dir.
        cancellationToken.ThrowIfCancellationRequested();
        var runDirectory = Path.Combine(request.Prepared.Layout.RootPath, "runs", runId);
        var logDirectory = Path.Combine(runDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        // run logger: a fresh per-run logger (the logger is per-run stateful and AgentHomeService is a singleton, so a
        // new instance is resolved per run from a short-lived scope). Logging is best-effort and never fails the run.
        using var loggerScope = _scopeFactory.CreateScope();
        var runLogger = loggerScope.ServiceProvider.GetRequiredService<IAgentHomeRunLogger>();
        var identity = await _identityProvider.GetAsync(cancellationToken);
        await OpenRunLogAsync(runLogger, runId, logDirectory, identity, cancellationToken);
        await AppendEventSafelyAsync(runLogger, "prepare_completed",
            string.Create(CultureInfo.InvariantCulture, $"goal_length={request.Goal.Length}"),
            cancellationToken);

        // The GOAL is what runs. The executor owns the inner loop, its tools (built from AllowedActions, so each value
        // gates a real capability) and the budgets; every tool works on the copied workspace, which is also every CWD.
        AgentHomeGoalOutcome goal;
        try
        {
            goal = await _goalExecutor.ExecuteAsync(new AgentHomeGoalRequest
                {
                    Handle = request.Prepared.Handle,
                    RunId = runId,
                    Goal = request.Goal,
                    AllowedActions = request.AllowedActions,
                    WorkspaceAliases = [.. CopiedWorkspaceAliases(request.Prepared.FolderSnapshots)],
                    RunLogger = runLogger
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A user/connection cancel. The sandbox tree-kills the in-flight command on the same token, so there is
            // nothing left running; record it and propagate so the lifecycle releases the lease.
            await AppendEventSafelyAsync(runLogger, "cancelled", detail: null, CancellationToken.None);
            throw;
        }

        await AppendEventSafelyAsync(runLogger, "goal_executed",
            string.Create(CultureInfo.InvariantCulture,
                $"status={goal.Status};tool_calls={goal.ToolCallCount};refused={goal.RefusedCallCount};commands={goal.Commands.Count};files_written={goal.WrittenFiles.Count}"),
            cancellationToken);

        // Export after the loop, so the agent's edits diff against the workspace-copy baseline. Gated on export_patch in
        // AllowedActions and on the baseline existing. A budget-cut run still exports: the partial work is real.
        var patch = await ExportPatchAsync(request, runId, runDirectory, runLogger, cancellationToken);

        var timedOut = goal.Status == AgentHomeGoalStatus.TimeBudgetExceeded;
        var completed = goal.Status is AgentHomeGoalStatus.Completed or AgentHomeGoalStatus.NotRun;

        await AppendEventSafelyAsync(runLogger, "run_completed",
            string.Create(CultureInfo.InvariantCulture, $"status={goal.Status};changed_files={patch.ChangedFileCount}"),
            cancellationToken);

        _logger.LogInformation("AgentHome run {RunId} finished: status={Status}, toolCalls={ToolCalls}, commands={Commands}, changedFiles={ChangedFiles}.",
            runId,
            goal.Status,
            goal.ToolCallCount,
            goal.Commands.Count,
            patch.ChangedFileCount);

        return new AgentHomeRunResult
        {
            RunId = runId,
            Completed = completed,
            TimedOut = timedOut,
            // The run-level exit code: 0 when the loop ended on its own terms, -1 when a budget or a failure cut it
            // off. Individual command exit codes ride GoalOutcome.Commands, where they belong.
            ExitCode = completed ? 0 : -1,
            LogPath = logDirectory,
            FolderSnapshots = request.Prepared.FolderSnapshots,
            Patch = patch,
            SandboxProviderName = _provider.ProviderName,
            GoalOutcome = goal
        };
    }

    private async Task<AgentHomePatchExport> ExportPatchAsync(AgentHomeRunRequest request,
        string runId,
        string runDirectory,
        IAgentHomeRunLogger runLogger,
        CancellationToken cancellationToken)
    {
        // The AgentHome gateway requires the model to grant export_patch. A Git baseline exists only after workspace copy, so
        // when at least one folder copied at least one file. Both must hold before a diff is attempted.
        if (!request.AllowedActions.Contains("export_patch", StringComparer.Ordinal))
        {
            return EmptyPatchExport;
        }

        var hasBaseline = request.Prepared.FolderSnapshots
                                 .Any(snapshot => snapshot is { Status: SelectedFolderCopyStatus.Copied, CopiedFileCount: > 0 });
        if (!hasBaseline)
        {
            return EmptyPatchExport;
        }

        return await _patchService.ExportPatchAsync(request.Prepared.Handle,
            new AgentHomePatchExportRequest
            {
                RunId = runId,
                HostRunDirectory = runDirectory,
                ResolvedFolders = request.Prepared.ResolvedFolders,
                RunLogger = runLogger
            },
            cancellationToken);
    }

    private async Task OpenRunLogAsync(IAgentHomeRunLogger runLogger,
        string runId,
        string logDirectory,
        AgentHomeOwnerIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            await runLogger.OpenAsync(new AgentHomeRunLogContext
                {
                    RunId = runId,
                    HostLogDirectory = logDirectory,
                    NodeId = identity.NodeId,
                    OwnerUserId = identity.OwnerUserId,
                    ProviderName = _provider.ProviderName
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort logging: a filesystem or permissions error must never fail the run.
            _logger.LogWarning(exception, "AgentHome run {RunId} could not open the run log.", runId);
        }
    }

    private async Task AppendEventSafelyAsync(IAgentHomeRunLogger runLogger, string eventName, string? detail, CancellationToken cancellationToken)
    {
        try
        {
            await runLogger.AppendEventAsync(eventName, detail, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort logging: a filesystem or permissions error must never fail the run.
            _logger.LogDebug(exception, "AgentHome run log append for event {EventName} failed.", eventName);
        }
        catch (InvalidOperationException exception)
        {
            // Logger context not opened (the open itself failed above); skip rather than mask the real outcome.
            _logger.LogDebug(exception, "AgentHome run log append for event {EventName} skipped (log not opened).", eventName);
        }
    }

    /// <summary>
    ///     The aliases of the folders that actually copied, so the goal loop's prompt can name the top-level
    ///     directories the model may work in. Aliases only — never a host path.
    /// </summary>
    private static IEnumerable<string> CopiedWorkspaceAliases(IReadOnlyList<SelectedFolderSnapshot> snapshots)
    {
        return snapshots.Where(static snapshot => snapshot is { Status: SelectedFolderCopyStatus.Copied, CopiedFileCount: > 0 })
                        .Select(static snapshot => snapshot.Alias);
    }

    private string ResolveRuntimeProfile(string? requestedProfile)
    {
        if (requestedProfile is not null && !string.Equals(requestedProfile, _options.DefaultRuntimeProfile, StringComparison.Ordinal))
        {
            throw new AgentHomeRequestRejectedException($"runtime profile '{requestedProfile}' is not enabled on this node.");
        }

        return _options.DefaultRuntimeProfile;
    }

    private async Task<IReadOnlyList<ResolvedSelectedFolder>> ResolveFoldersAsync(IReadOnlyList<string> selectedFolderIds,
        CancellationToken cancellationToken)
    {
        // The resolver is scoped (it owns a NodeChatDbContext) and this service is a singleton, so resolve every id inside
        // one short-lived scope. The DbContext is not thread-safe, so resolve sequentially.
        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ISelectedFolderResolver>();

        var resolved = new List<ResolvedSelectedFolder>(selectedFolderIds.Count);
        foreach (var id in selectedFolderIds)
        {
            resolved.Add(await resolver.ResolveAsync(id, cancellationToken));
        }

        return resolved;
    }

    private string CreateRunId()
    {
        var counter = Interlocked.Increment(ref _runCounter);
        var unixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return string.Create(CultureInfo.InvariantCulture,
            $"run-{unixMs}-{counter}");
    }
}
