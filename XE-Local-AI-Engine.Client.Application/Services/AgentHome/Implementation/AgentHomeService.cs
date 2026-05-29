namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Marker I-pre <see cref="IAgentHomeService" />. Drives the real orchestration end-to-end against the configured
///     provider (the deterministic fake by default): build the owner/node attach key, recover the worker-local layout,
///     attach/create the sandbox, resolve (not copy) the selected folders, then run one liveness-probe command under a
///     command timeout separate from the preparation timeout. Workspace copy (F), patch export (G), memory proposals
///     (H), run-scoped log content (K), and explicit cancel/kill cleanup + the <c>AgentHomeBusy</c> guard (I) extend
///     this body later.
/// </summary>
internal sealed class AgentHomeService : IAgentHomeService
{
    private const string ProbeExecutable = "dotnet";
    private static readonly string[] ProbeArguments = ["--version"];

    private static readonly AgentHomePatchExport EmptyPatchExport = new()
    {
        ChangedFileCount = 0,
        Blocked = false,
        PatchBytes = 0,
        PatchRelativePath = null,
        ChangedFilesRelativePath = null
    };

    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILogger<AgentHomeService> _logger;
    private readonly IAgentHomeManifestService _manifestService;
    private readonly AgentHomeOptions _options;
    private readonly IAgentHomePatchService _patchService;
    private readonly ISandboxRuntimeProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentHomeWorkspaceService _workspaceService;
    private int _runCounter;

    public AgentHomeService(
        IAgentHomeManifestService manifestService,
        ISandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        IAgentHomeWorkspaceService workspaceService,
        IAgentHomePatchService patchService,
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
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentHomePrepareResult> PrepareAsync(AgentHomePrepareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prepareCts.CancelAfter(TimeSpan.FromSeconds(_options.PrepareTimeoutSeconds));
        var prepareToken = prepareCts.Token;

        // Policy gates first: a disallowed runtime profile or an unknown/invalid selected-folder id is rejected before
        // any manifest or provider call (AgentHome plan §7 / §11 validation matrix).
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

        // Marker F: copy each resolved selected folder into the sandbox workspace (exclusions, symlink-escape guard,
        // per-folder byte budget, git baseline). Runs under the preparation timeout, separate from the command timeout.
        var folderSnapshots = await _workspaceService
            .PrepareSelectedFoldersAsync(handle, resolvedFolders, prepareToken).ConfigureAwait(false);

        _logger.LogInformation(
            "AgentHome prepared for node {NodeId}: sandbox {SandboxId}, {FolderCount} selected folder(s) resolved.",
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

        var commandRequest = new SandboxCommandRequest
        {
            ExecutionId = runId,
            Executable = ProbeExecutable,
            Arguments = ProbeArguments,
            // commandCts is the authoritative hard deadline (it works with every provider, including the fake which
            // ignores Timeout); request.Timeout carries the same budget as a hint a real provider may honor. Both
            // derive from the single CommandTimeoutSeconds value, so they cannot diverge.
            Timeout = commandTimeout
        };

        var result = await _provider.ExecuteAsync(request.Prepared.Handle, commandRequest, commandCts.Token).ConfigureAwait(false);

        // Marker G: after the command runs (the agent has edited files in later markers), export the diff against the
        // Marker F git baseline. The artifacts are written host-side under runs/<run-id>/patches/.
        var patch = await ExportPatchAsync(request, runId, runDirectory, commandCts.Token).ConfigureAwait(false);

        _logger.LogInformation(
            "AgentHome run {RunId} finished: completed={Completed}, exitCode={ExitCode}, changedFiles={ChangedFiles}.",
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
            Patch = patch
        };
    }

    private async Task<AgentHomePatchExport> ExportPatchAsync(
        AgentHomeRunRequest request,
        string runId,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        // Marker F creates the git baseline only when at least one folder copied at least one file. Recompute that
        // signal from the prepare result so an export is attempted only against an existing baseline (no diff against
        // a non-repository). The empty export carries zero changed files and null paths.
        var hasBaseline = request.Prepared.FolderSnapshots
            .Any(snapshot => snapshot is { Status: SelectedFolderCopyStatus.Copied, CopiedFileCount: > 0 });
        if (!hasBaseline)
        {
            return EmptyPatchExport;
        }

        return await _patchService.ExportPatchAsync(
            request.Prepared.Handle,
            new AgentHomePatchExportRequest
            {
                RunId = runId,
                HostRunDirectory = runDirectory,
                ResolvedFolders = request.Prepared.ResolvedFolders
            },
            cancellationToken).ConfigureAwait(false);
    }

    private string ResolveRuntimeProfile(string? requestedProfile)
    {
        if (requestedProfile is not null && !string.Equals(requestedProfile, _options.DefaultRuntimeProfile, StringComparison.Ordinal))
        {
            throw new AgentHomeRequestRejectedException(
                $"runtime profile '{requestedProfile}' is not enabled on this node.");
        }

        return _options.DefaultRuntimeProfile;
    }

    private async Task<IReadOnlyList<ResolvedSelectedFolder>> ResolveFoldersAsync(
        IReadOnlyList<string> selectedFolderIds,
        CancellationToken cancellationToken)
    {
        // The resolver is scoped (it owns a NodeChatDbContext); this service is a singleton, so resolve all ids within
        // a single short-lived scope. The DbContext is not thread-safe, so resolve sequentially. Marker F copies the
        // resolved folders into the sandbox; Marker I-pre resolves only (proves unknown-id rejection).
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
        return string.Create(
            CultureInfo.InvariantCulture,
            $"run-{unixMs}-{counter}");
    }
}
