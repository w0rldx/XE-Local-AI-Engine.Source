namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     AgentHome gateway <see cref="IAgentHomeService" />. Drives the real orchestration end-to-end against the configured
///     provider (the deterministic fake by default): <see cref="RunLifecycleAsync" /> resolves owner/node identity once,
///     acquires a run-level single-flight guard keyed by that owner-node, then runs Prepare + Run under it. Prepare
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
internal sealed class AgentHomeService : IAgentHomeService
{
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

    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILogger<AgentHomeService> _logger;
    private readonly IAgentHomeManifestService _manifestService;
    private readonly IAgentHomeMemoryProposalService _memoryProposalService;
    private readonly AgentHomeOptions _options;
    private readonly IAgentHomePatchService _patchService;
    private readonly ISandboxRuntimeProvider _provider;

    // Run-level single-flight guard: one semaphore per owner-node, created on demand. A second run for
    // the same owner-node while one is in flight is rejected (non-blocking Wait(0)), not queued. Keyed by a string so
    // the guard does not couple to SandboxAttachKey value-equality (which folds in ManifestVersion/RuntimeProfile).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runGuards = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentHomeWorkspaceService _workspaceService;
    private int _runCounter;

    public AgentHomeService(IAgentHomeManifestService manifestService,
        ISandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        IAgentHomeWorkspaceService workspaceService,
        IAgentHomePatchService patchService,
        IAgentHomeMemoryProposalService memoryProposalService,
        IServiceScopeFactory scopeFactory,
        IOptions<AgentHomeOptions> options,
        TimeProvider timeProvider,
        ILogger<AgentHomeService> logger)
    {
        _manifestService = manifestService ?? throw new ArgumentNullException(nameof(manifestService));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _patchService = patchService ?? throw new ArgumentNullException(nameof(patchService));
        _memoryProposalService = memoryProposalService ?? throw new ArgumentNullException(nameof(memoryProposalService));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentHomeRunResult> RunLifecycleAsync(AgentHomeRunLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Resolve identity FIRST so the guard key exists before Prepare. A second run for the same owner-node while one
        // is in flight is rejected, not queued.
        var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var guardKey = string.Create(CultureInfo.InvariantCulture, $"{identity.OwnerUserId} {identity.NodeId}");
        var guard = _runGuards.GetOrAdd(guardKey, static _ => new SemaphoreSlim(1, 1));

        // Non-blocking try-acquire: a zero timeout returns immediately rather than queueing a second concurrent run.
        if (!await guard.WaitAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false))
        {
            throw new AgentHomeBusyException("an AgentHome run is already in progress for this node.");
        }

        try
        {
            var prepared = await PrepareAsync(new AgentHomePrepareRequest
                {
                    SelectedFolderIds = request.SelectedFolderIds,
                    RuntimeProfile = request.RuntimeProfile
                },
                cancellationToken).ConfigureAwait(false);

            return await RunAsync(new AgentHomeRunRequest
                {
                    Prepared = prepared,
                    Goal = request.Goal,
                    AllowedActions = request.AllowedActions
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release covers success, timeout, cancel, and kill so the owner-node is not left permanently busy.
            guard.Release();
        }
    }

    public async Task<AgentHomePrepareResult> PrepareAsync(AgentHomePrepareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prepareCts.CancelAfter(TimeSpan.FromSeconds(_options.PrepareTimeoutSeconds));
        var prepareToken = prepareCts.Token;

        // Policy gates first: a disallowed runtime profile or an unknown/invalid selected-folder id is rejected before
        // any manifest or provider call.
        var effectiveProfile = ResolveRuntimeProfile(request.RuntimeProfile);
        var resolvedFolders = await ResolveFoldersAsync(request.SelectedFolderIds, prepareToken).ConfigureAwait(false);

        var identity = await _identityProvider.GetAsync(prepareToken).ConfigureAwait(false);
        var attachKey = new SandboxAttachKey
        {
            OwnerUserId = identity.OwnerUserId,
            NodeId = identity.NodeId,
            ProviderName = _provider.ProviderName,
            RuntimeProfile = effectiveProfile,
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };

        var layout = await _manifestService.InitializeAsync(attachKey, prepareToken).ConfigureAwait(false);

        var createRequest = new SandboxCreateRequest
        {
            AttachKey = attachKey,
            RuntimeProfile = effectiveProfile,
            NetworkPolicy = SandboxNetworkPolicy.None
        };
        var handle = await _provider.CreateOrAttachAsync(createRequest, prepareToken).ConfigureAwait(false);

        // workspace copy: copy each resolved selected folder into the sandbox workspace (exclusions, symlink-escape guard,
        // per-folder byte budget, git baseline). Runs under the preparation timeout, separate from the command timeout.
        var folderSnapshots = await _workspaceService
                                    .PrepareSelectedFoldersAsync(handle, resolvedFolders, prepareToken).ConfigureAwait(false);

        _logger.LogInformation("AgentHome prepared for node {NodeId}: sandbox {SandboxId}, {FolderCount} selected folder(s) resolved.",
            attachKey.NodeId,
            handle.SandboxId,
            resolvedFolders.Count);

        return new AgentHomePrepareResult
        {
            Layout = layout,
            Handle = handle,
            ResolvedFolders = resolvedFolders,
            FolderSnapshots = folderSnapshots,
            RuntimeProfile = effectiveProfile
        };
    }

    public async Task<AgentHomeRunResult> RunAsync(AgentHomeRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var runId = CreateRunId();
        var commandTimeout = TimeSpan.FromSeconds(_options.CommandTimeoutSeconds);

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
                await AppendEventSafelyAsync(runLogger, "cancelled", null, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            // Only commandCts fired (CancelAfter) → a TIMEOUT → surface a non-throwing result so the conversation can
            // continue. The patch/memory exports are skipped; the run logger records the timeout.
            await AppendCommandSafelyAsync(runLogger, runId, descriptor, false, -1, commandStartedAt,
                nameof(OperationCanceledException), CancellationToken.None).ConfigureAwait(false);
            await AppendEventSafelyAsync(runLogger, "timed_out",
                string.Create(CultureInfo.InvariantCulture, $"timeout_seconds={_options.CommandTimeoutSeconds}"),
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogWarning("AgentHome run {RunId} timed out after {TimeoutSeconds}s.",
                runId,
                _options.CommandTimeoutSeconds);

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
            null, cancellationToken).ConfigureAwait(false);

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
        // Gate 1 (AgentHome gateway): the model must have granted export_patch. Gate 2 (workspace copy/G): a git baseline only exists
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
