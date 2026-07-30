namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

/// <summary>
///     The <c>docker</c> sandbox <see cref="ISandboxRuntimeProvider" />: a container per sandbox, created under the
///     §3.8 hardening contract and <em>verified</em> against the daemon's own read-back before the handle is returned.
///     Permitted for Development Mode build/test/lint execution only, per ADR 0004.
///     <para>
///         Security posture: unlike <c>ProcessSandboxRuntimeProvider</c>, this provider does isolate — the container
///         gets its own filesystem, network and PID namespaces with capabilities dropped, no-new-privileges set, a
///         read-only root filesystem and enforced CPU/memory/PID ceilings. What it is not is a replacement for the
///         MXC seam: ADR 0004 records Docker as an interim backend behind <see cref="ISandboxRuntimeProvider" />, not
///         as the end of that seam, and on Linux the daemon socket this provider talks to is root-equivalent.
///     </para>
///     <para>
///         Fail-closed everywhere. If any single §3.8 guarantee cannot be read back off the created container, the
///         container is removed and the create is rejected with <see cref="SandboxCapabilityNotSupportedException" />.
///         There is no path through this class that returns a handle to a container it could not verify.
///     </para>
///     <para>
///         Phase 1 scope. This provider is registered but is NOT wired into Development Mode execution — that switch
///         is per-feature provider selection (D2), and <c>SandboxProviderSelector</c> is untouched. The mount broker
///         (D9 control-state exclusion, per-attempt HOME/temp/tool state), the standalone workspace clone (D8), the
///         dependency-manifest rejection (D6), lifecycle ownership and the startup reaper are all later work; what
///         exists here is a single engine-generated workspace bind mount, which is the minimum that makes the
///         hardening contract testable against a real daemon.
///     </para>
/// </summary>
// Implements the Development role ONLY, and that omission is load-bearing rather than an oversight: ADR 0004 permits
// Docker for Development Mode build/test/lint execution only, so this provider deliberately does NOT implement
// IAgentSandboxRuntimeProvider. Registering it for AgentHome or Coder is therefore a COMPILE ERROR, not something a
// reviewer has to notice — which is what keeps a container requirement from spreading to features D0 scopes it out of.
public sealed class DockerSandboxRuntimeProvider : IDevelopmentSandboxRuntimeProvider, IAsyncDisposable
{
    /// <summary>The provider name this registers under for configuration-bound selection.</summary>
    public const string Name = "docker";

    private const int DefaultMaxCapturedOutputBytes = 4 * 1024 * 1024;

    // On Windows the engine is a native Windows process while the container is Linux (D1), so the host's own account
    // identifiers do not name anything inside it. 1000 is the conventional first non-root Linux account and is only a
    // default: an operator whose image expects another id sets UserId/GroupId explicitly.
    private const int WindowsDefaultUserId = 1000;

    private readonly IDockerRuntimeClientFactory _clientFactory;
    private readonly ILogger<DockerSandboxRuntimeProvider> _logger;
    private readonly IOptionsMonitor<ContainerSandboxOptions> _options;
    private readonly ConcurrentDictionary<string, SandboxState> _sandboxes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public DockerSandboxRuntimeProvider(IOptionsMonitor<ContainerSandboxOptions> options,
        IDockerRuntimeClientFactory clientFactory,
        TimeProvider timeProvider,
        ILogger<DockerSandboxRuntimeProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ProviderName => Name;

    /// <summary>
    ///     Advertises only what this provider verifies on a real container, which is not the same as what it passes to
    ///     the daemon.
    ///     <para>
    ///         <see cref="SandboxProviderCapabilities.SupportsCopyInto" /> is deliberately absent. Docker refuses
    ///         <c>PUT /containers/{id}/archive</c> outright against a container with a read-only root filesystem —
    ///         measured against Engine 29.6.1, which answers <c>400 container rootfs is marked read-only</c>
    ///         regardless of the destination path, including a writable <c>tmpfs</c>. §3.8 makes the read-only root
    ///         filesystem non-negotiable, so copy-into is structurally unavailable to a conformant container and
    ///         advertising it would be a claim this provider cannot honour. Host-side transfer through the
    ///         engine-generated workspace mount is the route, and it belongs to the mount broker.
    ///     </para>
    /// </summary>
    public SandboxProviderCapabilities Capabilities =>
        SandboxProviderCapabilities.SupportsCopyOut
        | SandboxProviderCapabilities.SupportsReadOnlyMounts
        | SandboxProviderCapabilities.SupportsNetworkPolicy
        | SandboxProviderCapabilities.SupportsResourceLimits
        | SandboxProviderCapabilities.SupportsCommandCancellation
        | SandboxProviderCapabilities.SupportsAttach
        | SandboxProviderCapabilities.SupportsKill
        | SandboxProviderCapabilities.SupportsTrustedHostWorkspace;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        foreach (var state in _sandboxes.Values)
        {
            await TerminateAsync(state, CancellationToken.None).ConfigureAwait(false);
        }

        _sandboxes.Clear();
        _sync.Dispose();
    }

    public async Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;
        RejectUnservableRequest(request, options);

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sandboxId = BuildSandboxId(request.AttachKey);
            if (_sandboxes.TryGetValue(sandboxId, out var existing))
            {
                EnsureCompatibleWorkspaceBinding(existing, request.TrustedHostWorkspace);
                return existing.Handle;
            }

            await EvictOwnerConflictsAsync(request.AttachKey, cancellationToken).ConfigureAwait(false);
            return await CreateVerifiedAsync(request, options, sandboxId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachKey);
        cancellationToken.ThrowIfCancellationRequested();

        var sandboxId = BuildSandboxId(attachKey);
        if (_sandboxes.TryGetValue(sandboxId, out var state) && state.Handle.AttachKey == attachKey)
        {
            return Task.FromResult(state.Handle);
        }

        throw new SandboxHandleInvalidException("No live sandbox matches the supplied attach key.");
    }

    public async Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle,
        SandboxCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        var startedAt = _timeProvider.GetUtcNow();

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } timeout)
        {
            execution.CancelAfter(timeout);
        }

        if (!state.InFlight.TryAdd(request.ExecutionId, execution))
        {
            throw new InvalidOperationException($"Execution id '{request.ExecutionId}' is already in flight for this sandbox.");
        }

        try
        {
            var outcome = await state.Client.ExecuteAsync(state.ContainerId,
                                          new DockerExecutionRequest
                                          {
                                              Executable = request.Executable,
                                              Arguments = request.Arguments,
                                              // MAPPED, not forwarded. The caller's working directory is a path in the
                                              // sandbox namespace whose root is the workspace, so Development Mode's
                                              // literal "/" means the repository root and not the container's root —
                                              // which is where an unmapped forward would have run every command.
                                              WorkingDirectory = request.WorkingDirectory is null
                                                  ? state.WorkspaceMountTarget
                                                  : DockerSandboxPaths.ResolveContainerPath(state.WorkspaceMountTarget, request.WorkingDirectory),
                                              Environment = request.Environment,
                                              StandardInput = request.StandardInput,
                                              MaxCapturedBytes = DefaultMaxCapturedOutputBytes
                                          },
                                          execution.Token)
                                      .ConfigureAwait(false);

            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = (int)outcome.ExitCode,
                StandardOutput = outcome.StandardOutput,
                StandardError = outcome.StandardError,
                StandardOutputTruncated = outcome.StandardOutputTruncated,
                StandardErrorTruncated = outcome.StandardErrorTruncated,
                Completed = true,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancelled by CancelCommandAsync or by the per-command timeout. Reported as an incomplete result rather
            // than as an exception, matching the contract's Completed flag.
            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardError = "Command was cancelled before completion.",
                Completed = false,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }
        finally
        {
            state.InFlight.TryRemove(request.ExecutionId, out _);
        }
    }

    public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        // Not advertised, and therefore rejected rather than emulated. See the Capabilities documentation: Docker
        // refuses archive extraction into a container whose root filesystem is read-only, and §3.8 requires it to be.
        throw new SandboxCapabilityNotSupportedException(
            "The docker sandbox provider does not serve copy-into. Docker refuses archive extraction into a container with a "
            + "read-only root filesystem, and the §3.8 hardening contract requires one, so this provider does not advertise "
            + "SupportsCopyInto. Write through the engine-generated workspace mount on the host instead.");
    }

    public async Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);

        return await ReadFileAsync(handle, sandboxPath, DefaultMaxCapturedOutputBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadFileAsync(SandboxHandle handle,
        string sandboxPath,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var state = GetAliveState(handle);
        var containerPath = DockerSandboxPaths.ResolveContainerPath(state.WorkspaceMountTarget, sandboxPath);
        var outcome = await state.Client.ExecuteAsync(state.ContainerId,
                                      new DockerExecutionRequest
                                      {
                                          Executable = "cat",
                                          Arguments = [containerPath],
                                          // One byte over the caller's bound, so a file exactly at the bound is
                                          // returned while one over it is detected rather than silently trimmed.
                                          MaxCapturedBytes = maxBytes + 1
                                      },
                                      cancellationToken)
                                  .ConfigureAwait(false);

        if (outcome.ExitCode != 0)
        {
            throw new FileNotFoundException($"Sandbox path '{sandboxPath}' could not be read.", sandboxPath);
        }

        if (Encoding.UTF8.GetByteCount(outcome.StandardOutput) > maxBytes)
        {
            throw new InvalidDataException("The sandbox file exceeds the requested read bound.");
        }

        return outcome.StandardOutput;
    }

    public async Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        // Implemented as a bounded in-container read plus a host write rather than through the Docker archive API.
        // Measured reason: on a rootless daemon the archive endpoint fails with `remount-ro … operation not
        // permitted` for any path under a bind mount — which is where every interesting artifact lives.
        var content = await ReadFileAsync(handle, request.SourcePath, DefaultMaxCapturedOutputBytes, cancellationToken)
                          .ConfigureAwait(false);

        var directory = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(request.DestinationPath, content, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        if (state.InFlight.TryGetValue(executionId, out var execution))
        {
            await execution.CancelAsync().ConfigureAwait(false);
        }
    }

    public async Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (_sandboxes.TryRemove(handle.SandboxId, out var state))
        {
            await TerminateAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Resolve the in-container UID/GID. An unset value takes the engine process's own effective ids, which is the
    ///     pairing that makes an engine-generated bind mount usable under a conventional rootful daemon. Zero is
    ///     rejected outright, from either source: §3.8 requires non-root execution, and silently running as root
    ///     because the engine happens to be running as root is exactly the fail-open this contract forbids.
    /// </summary>
    internal static ResolvedContainerIdentity ResolveIdentity(ContainerSandboxOptions options, Func<int> userIdReader, Func<int> groupIdReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(userIdReader);
        ArgumentNullException.ThrowIfNull(groupIdReader);

        var userId = options.UserId ?? userIdReader();
        var groupId = options.GroupId ?? groupIdReader();

        if (userId <= 0 || groupId <= 0)
        {
            throw new SandboxCapabilityNotSupportedException(
                $"The docker sandbox provider refuses to create a container as uid {userId}, gid {groupId}. The §3.8 hardening "
                + "contract requires non-root execution with an explicit UID and GID. Set "
                + $"'{ContainerSandboxOptions.SectionName}:UserId' and ':GroupId' to non-zero values.");
        }

        return new ResolvedContainerIdentity(userId, groupId);
    }

    private static ResolvedContainerIdentity ResolveIdentity(ContainerSandboxOptions options)
    {
        return ResolveIdentity(options,
            static () => OperatingSystem.IsWindows() ? WindowsDefaultUserId : (int)GetEffectiveUserId(),
            static () => OperatingSystem.IsWindows() ? WindowsDefaultUserId : (int)GetEffectiveGroupId());
    }

    // DllImport rather than the source-generated LibraryImport, matching ProcessSandboxRuntimeProvider: the generated
    // form requires AllowUnsafeBlocks on the whole project, and neither of these takes a pointer or a buffer.
    // geteuid/getegid cannot fail and so need no SetLastError.
    [DllImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getegid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveGroupId();

    /// <summary>
    ///     Reject, up front, every request this provider cannot serve exactly as asked. Rejecting before creating is
    ///     not merely tidier — a request for an un-isolated network that got as far as a created container would leave
    ///     the caller reasoning about a container that should never have existed.
    /// </summary>
    private static void RejectUnservableRequest(SandboxCreateRequest request, ContainerSandboxOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Image))
        {
            throw new SandboxCapabilityNotSupportedException(
                "The docker sandbox provider has no approved container image configured. Set "
                + $"'{ContainerSandboxOptions.SectionName}:Image' to a digest-pinned reference.");
        }

        if (request.TrustedHostWorkspace is null)
        {
            // Phase 1 creates exactly one engine-generated mount, and it is the workspace. Without it the container
            // would have nothing to act on, and a container with no workspace is not a sandbox, it is an idle process.
            throw new SandboxCapabilityNotSupportedException(
                "The docker sandbox provider requires an engine-managed trusted host workspace on the create request.");
        }

        if (request.NetworkPolicy != SandboxNetworkPolicy.None)
        {
            // `Restricted` has no mechanism here — an egress allow-list is the v2 package-proxy project (D6) — and
            // `Unrestricted` is not on offer: this provider exists to confine, and handing back a container that
            // shares the host network would be a weaker sandbox than the caller asked for by any reading.
            throw new SandboxCapabilityNotSupportedException(
                $"The docker sandbox provider serves only {nameof(SandboxNetworkPolicy.None)}; "
                + $"'{request.NetworkPolicy}' was requested. Agent-facing execution runs with the network off (plan D6); "
                + "a restricted egress allow-list is separate, later work.");
        }
    }

    private async Task<SandboxHandle> CreateVerifiedAsync(SandboxCreateRequest request,
        ContainerSandboxOptions options,
        string sandboxId,
        CancellationToken cancellationToken)
    {
        var identity = ResolveIdentity(options);
        var workspaceRoot = Path.GetFullPath(request.TrustedHostWorkspace!.RootPath);
        Directory.CreateDirectory(workspaceRoot);

        var specification = DockerSandboxHardening.BuildSpecification(options,
            identity,
            "xe-dev-" + sandboxId,
            sandboxId,
            [
                new DockerBindMount
                {
                    HostPath = workspaceRoot,
                    ContainerPath = options.WorkspaceMountTarget,
                    ReadOnly = false,
                    Propagation = DockerSandboxHardening.PrivateMountPropagation
                }
            ],
            // Honored, not ignored. This provider advertises SupportsResourceLimits, so a caller's ceiling must be the
            // ceiling that gets applied — and because it is baked into the specification, the read-back verification
            // below checks the caller's numbers rather than the engine's defaults.
            request.ResourceLimits);

        var endpoint = DockerDaemonEndpointResolver.Resolve(options);
        var client = _clientFactory.Create(endpoint);
        string? containerId = null;

        try
        {
            containerId = await client.CreateContainerAsync(specification, cancellationToken).ConfigureAwait(false);
            await client.StartContainerAsync(containerId, cancellationToken).ConfigureAwait(false);

            var observed = await client.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
            var violations = DockerSandboxHardening.FindViolations(specification, observed);
            if (violations.Count > 0)
            {
                // The whole point of the read-back. The container exists and is running, and it is still refused,
                // because a container that is not the container we asked for is not usable evidence of anything.
                _logger.LogError("Refusing container {ContainerId}: {ViolationCount} hardening guarantee(s) could not be verified.",
                    containerId,
                    violations.Count);

                throw new SandboxCapabilityNotSupportedException(
                    "The docker sandbox provider created a container whose isolation settings could not be verified against the "
                    + "daemon's own read-back, so it was removed rather than used. Unverified guarantees: "
                    + string.Join(" ", violations));
            }

            var handle = new SandboxHandle
            {
                ProviderName = Name,
                SandboxId = sandboxId,
                AttachKey = request.AttachKey,
                CreatedAt = _timeProvider.GetUtcNow(),
                ManifestVersion = request.AttachKey.ManifestVersion
            };

            _sandboxes[sandboxId] = new SandboxState(handle, client, containerId, workspaceRoot, options.WorkspaceMountTarget);
            _logger.LogInformation("Created verified sandbox container {ContainerId} for sandbox {SandboxId}.", containerId, sandboxId);
            return handle;
        }
        catch
        {
            if (containerId is not null)
            {
                await SafeRemoveAsync(client, containerId).ConfigureAwait(false);
            }

            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task SafeRemoveAsync(IDockerRuntimeClient client, string containerId)
    {
        try
        {
            await client.RemoveContainerAsync(containerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DockerRuntimeException exception)
        {
            // A container that could not be removed is a leak, not a reason to swallow the original rejection. Logged
            // loudly; the startup reaper is the thing that finally collects it, and it is later work.
            _logger.LogError(exception, "Failed to remove container {ContainerId} after a fail-closed create.", containerId);
        }
    }

    private static void EnsureCompatibleWorkspaceBinding(SandboxState state, SandboxTrustedHostWorkspace? workspace)
    {
        var requested = workspace is null ? null : Path.GetFullPath(workspace.RootPath);
        if (!string.Equals(state.WorkspaceRoot, requested, StringComparison.Ordinal))
        {
            throw new SandboxCapabilityNotSupportedException(
                "An existing sandbox for this attach key is bound to a different trusted host workspace. Kill it before rebinding.");
        }
    }

    /// <summary>
    ///     An owner change on the same node forbids reuse: kill and remove any sandbox keyed to that node under a
    ///     different owner before creating the new one. Awaited rather than blocked on — this runs while the create
    ///     semaphore is held, and a sync-over-async wait there would turn a slow daemon into a deadlocked provider.
    /// </summary>
    private async Task EvictOwnerConflictsAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken)
    {
        var conflicts = _sandboxes
                        .Where(entry => string.Equals(entry.Value.Handle.AttachKey.NodeId, attachKey.NodeId, StringComparison.Ordinal)
                                        && !string.Equals(entry.Value.Handle.AttachKey.OwnerUserId, attachKey.OwnerUserId, StringComparison.Ordinal))
                        .Select(entry => entry.Key)
                        .ToArray();

        foreach (var conflicting in conflicts)
        {
            if (_sandboxes.TryRemove(conflicting, out var state))
            {
                await TerminateAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private SandboxState GetAliveState(SandboxHandle handle)
    {
        return _sandboxes.TryGetValue(handle.SandboxId, out var state)
            ? state
            : throw new SandboxHandleInvalidException($"Sandbox '{handle.SandboxId}' is no longer available.");
    }

    private async Task TerminateAsync(SandboxState state, CancellationToken cancellationToken)
    {
        foreach (var execution in state.InFlight.Values)
        {
            await execution.CancelAsync().ConfigureAwait(false);
        }

        state.InFlight.Clear();

        try
        {
            await state.Client.RemoveContainerAsync(state.ContainerId, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerRuntimeException exception)
        {
            _logger.LogError(exception, "Failed to remove sandbox container {ContainerId}.", state.ContainerId);
        }
        finally
        {
            await state.Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     A stable, filesystem- and Docker-name-safe id derived from the attach key. Hashed rather than concatenated
    ///     because the key carries a user id, and a container name is visible to anyone who can list containers.
    /// </summary>
    private static string BuildSandboxId(SandboxAttachKey attachKey)
    {
        var material = string.Join('\u001F',
            attachKey.OwnerUserId,
            attachKey.NodeId,
            attachKey.ProviderName,
            attachKey.RuntimeProfile,
            attachKey.ManifestVersion.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    private sealed class SandboxState
    {
        public SandboxState(SandboxHandle handle,
            IDockerRuntimeClient client,
            string containerId,
            string workspaceRoot,
            string workspaceMountTarget)
        {
            Handle = handle;
            Client = client;
            ContainerId = containerId;
            WorkspaceRoot = workspaceRoot;
            WorkspaceMountTarget = workspaceMountTarget;
        }

        public SandboxHandle Handle { get; }

        public IDockerRuntimeClient Client { get; }

        public string ContainerId { get; }

        public string WorkspaceRoot { get; }

        public string WorkspaceMountTarget { get; }

        public ConcurrentDictionary<string, CancellationTokenSource> InFlight { get; } = new(StringComparer.Ordinal);
    }
}
