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
///     AgentHome gateway <see cref="IAgentHomeService" />. Drives the real orchestration end-to-end against the configured
///     provider (the deterministic fake by default): <see cref="RunLifecycleAsync" /> resolves owner/node identity once,
///     acquires the shared exclusive execution lease keyed by that owner-node, then runs Prepare + Run under it. Prepare
///     builds the attach key, recovers the worker-local layout, attaches/creates the sandbox, resolves and copies
///     the selected folders, and creates the git baseline. Run executes one bounded, profile-driven command — with the
///     copied workspace as its working directory so the post-run patch export diffs the real CWD — classifies
///     timeout-vs-cancel, and feeds the gated patch export, memory proposal export, and run-scoped logging.
///     <para>
///         Deferred to the HostAgent-backed local-container provider: a real nested MAF agent loop, real
///         <c>dotnet build/test</c> and real git, multi-command sequences, and per-command enforcement. The
///         service-level tests prove worker-side orchestration plus busy/cancel/owner hardening on the fake provider;
///         the single command is the profile's liveness/work probe, and <c>allowedActions</c> gates the optional patch
///         and memory exports.
///     </para>
/// </summary>
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

    // Per-RuntimeProfile in-sandbox command descriptor. The current slice carries a single enabled profile; keeping it
    // keyed lets the HostAgent-backed provider add real per-profile commands without re-touching RunAsync. The command
    // is the profile's liveness/work probe on the fake.
    private static readonly IReadOnlyDictionary<string, AgentHomeCommandDescriptor> ProfileCommands =
        new Dictionary<string, AgentHomeCommandDescriptor>(StringComparer.Ordinal)
        {
            ["dotnet-agent-home"] = new("dotnet", ["--version"])
        };

    private readonly ComputeOptions _ceilingDefaults;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly IAgentHomeExecutionLeaseManager _leaseManager;
    private readonly IAgentHomeWorkspaceIsolation _isolation;
    private readonly ILogger<AgentHomeService> _logger;
    private readonly IAgentHomeManifestService _manifestService;
    private readonly IAgentHomeMemoryProposalService _memoryProposalService;
    private readonly AgentHomeOptions _options;
    private readonly IAgentHomePatchService _patchService;
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
        IAgentHomeMemoryProposalService memoryProposalService,
        IServiceScopeFactory scopeFactory,
        IOptions<AgentHomeOptions> options,
        IOptions<SandboxOptions> sandboxOptions,
        IOptions<ComputeOptions> ceilingDefaults,
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
        _memoryProposalService = memoryProposalService ?? throw new ArgumentNullException(nameof(memoryProposalService));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ArgumentNullException.ThrowIfNull(sandboxOptions);
        _sandboxOptions = sandboxOptions.Value;
        ArgumentNullException.ThrowIfNull(ceilingDefaults);
        _ceilingDefaults = ceilingDefaults.Value;
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
        var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
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
            var prepared = await PrepareUnderLeaseAsync(prepareRequest, attachKey, effectiveProfile, cancellationToken).ConfigureAwait(false);
            var result = await RunAsync(new AgentHomeRunRequest
                {
                    Prepared = prepared,
                    Goal = request.Goal,
                    AllowedActions = request.AllowedActions
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.Completed || result.TimedOut)
            {
                _ = await _isolation.ClearAsync(prepared.Handle, key, CancellationToken.None).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await RecoverAfterFailureAsync(attachKey, key).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AgentHomePrepareResult> PrepareUnderLeaseAsync(AgentHomePrepareRequest request,
        SandboxAttachKey attachKey,
        string effectiveProfile,
        CancellationToken cancellationToken)
    {
        var prepareTimeoutSeconds = await _runtimeSettings.GetAgentHomePrepareTimeoutSecondsAsync(cancellationToken).ConfigureAwait(false);
        using var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prepareCts.CancelAfter(TimeSpan.FromSeconds(prepareTimeoutSeconds));
        var prepareToken = prepareCts.Token;

        var layout = await _manifestService.InitializeAsync(attachKey, prepareToken).ConfigureAwait(false);

        var createRequest = new SandboxCreateRequest
        {
            AttachKey = attachKey,
            RuntimeProfile = effectiveProfile,
            // Default-deny egress wherever the provider can actually enforce it. The rule this rests on: everything
            // AgentHome and Coder run inside the sandbox is local — `dotnet --version`, git
            // init/config/add/commit/diff under /dev/null hooks, and Coder's find/grep — so denying egress costs no
            // supported capability and removes the sandbox child's reach to the node's own loopback API, the LAN, and
            // the cloud-metadata endpoint.
            //
            // The choice is CAPABILITY-GATED rather than unconditional: the provider fails closed on a confinement
            // request it cannot honor, so asking for None on a host without user namespaces (or on Windows, where the
            // mechanism is not implemented) would not harden AgentHome — it would stop it running at all. Asking for
            // exactly what the provider advertises keeps the guarantee real where it exists and keeps AgentHome working
            // where it does not, with the degradation visible in the sandbox containment log rather than silent. A node
            // that wants the refusal instead sets AgentHome:Sandbox:RequireEgressDenial, which SandboxEgressPolicy
            // turns into a fail-closed refusal naming that key.
            NetworkPolicy = SandboxEgressPolicy.Resolve(_provider.Capabilities,
                _sandboxOptions.RequireEgressDenial,
                SandboxEgressPolicy.AgentOptionKey,
                SandboxWorkloads.AgentHome.Workload),

            // The node's ceilings wherever the backend can impose them, derived through the one helper every create
            // site shares so this request cannot disagree with SandboxWorkloads.AgentHome's declaration.
            ResourceLimits = SandboxResourceCeilings.Resolve(SandboxWorkloads.AgentHome, _provider.Capabilities, _ceilingDefaults)
        };
        var handle = await _provider.CreateOrAttachAsync(createRequest, prepareToken).ConfigureAwait(false);

        // Clear before resolution so preparation never reasons over a prior selection. The workspace service resets
        // again immediately before copying; the lifecycle catch performs final recovery on every failure.
        await _workspaceService.PrepareSelectedFoldersAsync(handle, [], prepareToken).ConfigureAwait(false);
        return await PrepareAttachedAsync(request, effectiveProfile, attachKey, layout, handle, prepareToken).ConfigureAwait(false);
    }

    private async Task<AgentHomePrepareResult> PrepareAttachedAsync(AgentHomePrepareRequest request,
        string effectiveProfile,
        SandboxAttachKey attachKey,
        AgentHomeLayout layout,
        SandboxHandle handle,
        CancellationToken prepareToken)
    {
        var resolvedFolders = await ResolveFoldersAsync(request.SelectedFolderIds, prepareToken).ConfigureAwait(false);

        // Stage this conversation's uploaded attachments (the extracted, decrypted Markdown) as a synthetic read-only
        // "attachments" folder so the agent's existing file tools (list_files/read_file/search_text) discover them.
        // The staging snapshot holds DECRYPTED plaintext in a temp dir; it is disposed in the finally immediately after
        // the workspace copy completes so the plaintext never outlives the copy into the sandbox.
        var foldersToCopy = resolvedFolders;
        IReadOnlyList<string> stagedAttachmentPaths = [];
        IConversationStagingSnapshot? attachmentsSnapshot = null;
        IReadOnlyList<SelectedFolderSnapshot> folderSnapshots;
        try
        {
            attachmentsSnapshot = await TryStageConversationAttachmentsAsync(request.ConversationId, prepareToken).ConfigureAwait(false);
            if (attachmentsSnapshot is not null)
            {
                foldersToCopy =
                [
                    .. resolvedFolders,
                    new ResolvedSelectedFolder(Guid.NewGuid(), AttachmentsFolderAlias, attachmentsSnapshot.HostPath, SelectedFolderMode.Copy)
                ];

                // Capture the workspace-relative staged paths before the snapshot is disposed, so the chat agent-mode
                // path can point the model straight at them (e.g. attachments/report.md).
                stagedAttachmentPaths =
                [
                    .. attachmentsSnapshot.FileNames.Select(name => string.Create(CultureInfo.InvariantCulture, $"{AttachmentsFolderAlias}/{name}"))
                ];
            }

            // workspace copy: copy each resolved selected folder into the sandbox workspace (exclusions, symlink-escape
            // guard, per-folder byte budget, git baseline). Runs under the preparation timeout, separate from the command
            // timeout.
            folderSnapshots = await _workspaceService
                                    .PrepareSelectedFoldersAsync(handle, foldersToCopy, prepareToken).ConfigureAwait(false);
        }
        finally
        {
            if (attachmentsSnapshot is not null)
            {
                await attachmentsSnapshot.DisposeAsync().ConfigureAwait(false);
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

        var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var key = LeaseKey(identity);
        if (_leaseManager.IsPoisoned(key))
        {
            return new ConversationSandboxPreparation([], lease: null, isBusy: true);
        }

        var lease = _leaseManager.TryAcquire(key);

        // Share the owner-node execution lease with RunLifecycleAsync so an in-flight run_in_agent_home run and a
        // chat-mode re-stage cannot race on the same owner-node sandbox. Non-blocking: if another operation holds the
        // lease, skip the re-stage (the coder tools will report no workspace) rather than block the chat turn.
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
            }, attachKey, effectiveProfile, cancellationToken).ConfigureAwait(false);
            return new ConversationSandboxPreparation(prepared.StagedAttachmentRelativePaths, lease);
        }
        catch
        {
            await RecoverAfterFailureAsync(CreateAttachKey(identity, ResolveRuntimeProfile(requestedProfile: null)), key).ConfigureAwait(false);
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
            await _isolation.RecoverExistingAsync(attachKey, key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AgentHomeWorkspacePoisonedException exception)
        {
            _logger.LogError(exception, "AgentHome failure cleanup could not prove workspace isolation for node {NodeId}.", attachKey.NodeId);
        }
    }

    // Builds a decrypted-Markdown staging snapshot for the conversation's uploaded attachments, or null when there is
    // no conversation context or the conversation has no extracted files. The caller appends the snapshot's host path as
    // a synthetic folder and disposes the snapshot once the copy is done. Logs only counts/aliases — never host paths or
    // file content.
    private async Task<IConversationStagingSnapshot?> TryStageConversationAttachmentsAsync(Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId is not { } resolvedConversationId)
        {
            return null;
        }

        var files = await _uploadedFileStore.ListAsync(resolvedConversationId, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return null;
        }

        var snapshot = await _uploadedFileStore.CreateStagingSnapshotAsync(resolvedConversationId, cancellationToken).ConfigureAwait(false);
        if (snapshot.FileCount == 0)
        {
            // No extracted Markdown was cached (e.g. every file was unsupported/failed): nothing to stage. Dispose the
            // empty temp dir rather than leave it to the caller.
            await snapshot.DisposeAsync().ConfigureAwait(false);
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
        var commandTimeoutSeconds = await _runtimeSettings.GetAgentHomeCommandTimeoutSecondsAsync(cancellationToken).ConfigureAwait(false);
        var commandTimeout = TimeSpan.FromSeconds(commandTimeoutSeconds);

        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandCts.CancelAfter(commandTimeout);

        // Re-check cancellation before touching the host filesystem so an early cancel leaves no orphaned run dir.
        commandCts.Token.ThrowIfCancellationRequested();
        var runDirectory = Path.Combine(request.Prepared.Layout.RootPath, "runs", runId);
        var logDirectory = Path.Combine(runDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        // run logger: a fresh per-run logger (the logger is per-run stateful and AgentHomeService is a singleton, so a
        // new instance is resolved per run from a short-lived scope). Logging is best-effort and never fails the run.
        using var loggerScope = _scopeFactory.CreateScope();
        var runLogger = loggerScope.ServiceProvider.GetRequiredService<IAgentHomeRunLogger>();
        var identity = await _identityProvider.GetAsync(commandCts.Token).ConfigureAwait(false);
        await OpenRunLogAsync(runLogger, runId, logDirectory, identity, cancellationToken).ConfigureAwait(false);
        await AppendEventSafelyAsync(runLogger, "prepare_completed",
            string.Create(CultureInfo.InvariantCulture, $"goal_length={request.Goal.Length}"),
            cancellationToken).ConfigureAwait(false);

        // The single command is sourced from a per-profile descriptor. When a folder copied, run it with the copied
        // workspace as the CWD so patch export diffs the real working tree.
        var descriptor = ResolveCommandDescriptor(request.Prepared.RuntimeProfile);
        var hasWorkspace = HasCopiedWorkspace(request.Prepared.FolderSnapshots);
        var commandRequest = new SandboxCommandRequest
        {
            ExecutionId = runId,
            Executable = descriptor.Executable,
            Arguments = descriptor.Arguments,
            WorkingDirectory = hasWorkspace ? AgentHomeGit.WorkspaceSelectedRoot : null,
            // commandCts is the authoritative hard deadline (it works with every provider, including the fake which
            // ignores Timeout); request.Timeout carries the same budget as a hint a real provider may honor. Both
            // derive from the single CommandTimeoutSeconds value, so they cannot diverge.
            Timeout = commandTimeout
        };

        var commandStartedAt = _timeProvider.GetTimestamp();
        SandboxCommandResult result;
        try
        {
            result = await _provider.ExecuteAsync(request.Prepared.Handle, commandRequest, commandCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort cancel of the in-flight command (run id == execution id). Use a fresh token so a cancelled
            // caller token does not abort the cleanup itself. Do NOT KillAsync: the sandbox is owner-node-scoped and
            // reused across runs, so a normal cancel/timeout must not force a full re-prepare next run.
            await CancelInFlightCommandSafelyAsync(request.Prepared.Handle, runId).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                // The ORIGINAL caller token fired → a user/connection cancel → propagate.
                await AppendEventSafelyAsync(runLogger, "cancelled", detail: null, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            // Only commandCts fired (CancelAfter) → a TIMEOUT → surface a non-throwing result so the conversation can
            // continue. The patch/memory exports are skipped; the run logger records the timeout.
            await AppendCommandSafelyAsync(runLogger, runId, descriptor, completed: false, exitCode: -1, commandStartedAt,
                nameof(OperationCanceledException), CancellationToken.None).ConfigureAwait(false);
            await AppendEventSafelyAsync(runLogger, "timed_out",
                string.Create(CultureInfo.InvariantCulture, $"timeout_seconds={commandTimeoutSeconds}"),
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogWarning("AgentHome run {RunId} timed out after {TimeoutSeconds}s.",
                runId,
                commandTimeoutSeconds);

            return new AgentHomeRunResult
            {
                RunId = runId,
                Completed = false,
                TimedOut = true,
                ExitCode = -1,
                LogPath = logDirectory,
                FolderSnapshots = request.Prepared.FolderSnapshots,
                Patch = EmptyPatchExport
            };
        }

        await AppendCommandSafelyAsync(runLogger, runId, descriptor, result.Completed, result.ExitCode, commandStartedAt,
            errorClass: null, cancellationToken).ConfigureAwait(false);

        // Patch export runs after the command so the agent's file edits are diffed against the workspace-copy git
        // baseline — gated on export_patch ∈ AllowedActions (in addition to the baseline-exists gate).
        var patch = await ExportPatchAsync(request, runId, runDirectory, commandCts.Token).ConfigureAwait(false);

        // memory-proposal export: collect the agent-written memory proposals — gated on propose_memory ∈ AllowedActions.
        await CollectMemoryProposalsAsync(request, runId, runDirectory, runLogger, commandCts.Token).ConfigureAwait(false);

        await AppendEventSafelyAsync(runLogger, "run_completed",
            string.Create(CultureInfo.InvariantCulture, $"exit_code={result.ExitCode};changed_files={patch.ChangedFileCount}"),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("AgentHome run {RunId} finished: completed={Completed}, exitCode={ExitCode}, changedFiles={ChangedFiles}.",
            runId,
            result.Completed,
            result.ExitCode,
            patch.ChangedFileCount);

        return new AgentHomeRunResult
        {
            RunId = runId,
            Completed = result.Completed,
            ExitCode = result.ExitCode,
            LogPath = logDirectory,
            FolderSnapshots = request.Prepared.FolderSnapshots,
            Patch = patch
        };
    }

    private async Task<AgentHomePatchExport> ExportPatchAsync(AgentHomeRunRequest request,
        string runId,
        string runDirectory,
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
                ResolvedFolders = request.Prepared.ResolvedFolders
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CollectMemoryProposalsAsync(AgentHomeRunRequest request,
        string runId,
        string runDirectory,
        IAgentHomeRunLogger runLogger,
        CancellationToken cancellationToken)
    {
        // Gated on propose_memory ∈ AllowedActions. On the fake the agent writes nothing, so the collector returns an
        // empty result; the call proves the gate + wiring. Collection never mutates real memory and never throws on a
        // bad record (rejections are returned), so a logging best-effort wrapper is enough.
        if (!request.AllowedActions.Contains("propose_memory", StringComparer.Ordinal))
        {
            return;
        }

        var collected = await _memoryProposalService.CollectAsync(new MemoryProposalCollectRequest
            {
                RunId = runId,
                HostRunDirectory = runDirectory
            },
            cancellationToken).ConfigureAwait(false);

        await AppendEventSafelyAsync(runLogger, "memory_collected",
            string.Create(CultureInfo.InvariantCulture, $"proposals={collected.Proposals.Count};rejections={collected.Rejections.Count}"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CancelInFlightCommandSafelyAsync(SandboxHandle handle, string runId)
    {
        try
        {
            await _provider.CancelCommandAsync(handle, runId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SandboxHandleInvalidException exception)
        {
            // The sandbox is already gone (e.g. an owner-mismatch reset killed it). Nothing to cancel.
            _logger.LogDebug(exception, "AgentHome run {RunId} cancel cleanup found no live sandbox.", runId);
        }
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
                cancellationToken).ConfigureAwait(false);
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
            await runLogger.AppendEventAsync(eventName, detail, cancellationToken).ConfigureAwait(false);
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

    private async Task AppendCommandSafelyAsync(IAgentHomeRunLogger runLogger,
        string runId,
        AgentHomeCommandDescriptor descriptor,
        bool completed,
        int exitCode,
        long startedTimestamp,
        string? errorClass,
        CancellationToken cancellationToken)
    {
        var elapsed = _timeProvider.GetElapsedTime(startedTimestamp);
        try
        {
            await runLogger.AppendCommandAsync(new AgentHomeCommandLogRecord
                {
                    TimestampUtc = _timeProvider.GetUtcNow(),
                    ExecutionId = runId,
                    Executable = descriptor.Executable,
                    Arguments = descriptor.Arguments,
                    Completed = completed,
                    ExitCode = exitCode,
                    DurationMs = (long)elapsed.TotalMilliseconds,
                    ErrorClass = errorClass
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort logging: a filesystem or permissions error must never fail the run.
            _logger.LogDebug(exception, "AgentHome run {RunId} command log append failed.", runId);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogDebug(exception, "AgentHome run {RunId} command log append skipped (log not opened).", runId);
        }
    }

    private static AgentHomeCommandDescriptor ResolveCommandDescriptor(string runtimeProfile)
    {
        // The profile was already validated against the worker default in PrepareAsync, so the lookup should always
        // hit; the explicit throw guards a future profile added to options but not to the descriptor table.
        return ProfileCommands.TryGetValue(runtimeProfile, out var descriptor)
            ? descriptor
            : throw new AgentHomeRequestRejectedException($"no command descriptor is registered for runtime profile '{runtimeProfile}'.");
    }

    private static bool HasCopiedWorkspace(IReadOnlyList<SelectedFolderSnapshot> snapshots)
    {
        return snapshots.Any(snapshot => snapshot is { Status: SelectedFolderCopyStatus.Copied, CopiedFileCount: > 0 });
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
        // The resolver is scoped (it owns a NodeChatDbContext); this service is a singleton, so resolve all ids within
        // a single short-lived scope. The DbContext is not thread-safe, so resolve sequentially. workspace copy copies the
        // resolved folders into the sandbox.
        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ISelectedFolderResolver>();

        var resolved = new List<ResolvedSelectedFolder>(selectedFolderIds.Count);
        foreach (var id in selectedFolderIds)
        {
            resolved.Add(await resolver.ResolveAsync(id, cancellationToken).ConfigureAwait(false));
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

    /// <summary>The executable and arguments for a runtime profile's in-sandbox command.</summary>
    private sealed record AgentHomeCommandDescriptor(string Executable, IReadOnlyList<string> Arguments);
}
