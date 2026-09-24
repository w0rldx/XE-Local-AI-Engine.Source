namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The <c>docker</c> sandbox <see cref="ISandboxRuntimeProvider" />: a container per sandbox, created under the
///     Docker hardening contract and verified against the daemon's own read-back before the handle is returned.
/// </summary>
/// <remarks>
///     Permitted for Development Mode build/test/lint execution only, per ADR 0004, which records Docker as an
///     interim backend behind <see cref="ISandboxRuntimeProvider" /> rather than the end of that seam — and on Linux
///     the daemon socket this provider talks to is root-equivalent. Fail-closed everywhere: a hardening guarantee
///     that cannot be read back removes the container and rejects the create. Every enforced invariant, and the
///     egress mapping in particular, is in <c>docs/adr/0004-…</c> under "Invariants the code enforces".
/// </remarks>
// Implements the Development role ONLY. It does not implement IAgentSandboxRuntimeProvider, so registering it for
// AgentHome or Coder is a COMPILE ERROR rather than something a reviewer has to notice.
public sealed class DockerSandboxRuntimeProvider : IDevelopmentSandboxRuntimeProvider, IAsyncDisposable
{
    /// <summary>The provider name this registers under for configuration-bound selection.</summary>
    public const string Name = "docker";

    private const int DefaultMaxCapturedOutputBytes = 4 * 1024 * 1024;

    // The mapping probe runs `touch`, whose entire useful output is an error line: a small ceiling keeps a
    // misbehaving image from turning a create-time check into a multi-megabyte capture.
    private const int ProbeCapturedOutputBytes = 4 * 1024;

    // On Windows the engine is a native Windows process while the container is Linux, so host account identifiers
    // name nothing inside it. 1000 is the conventional first non-root Linux account and only a default.
    private const int WindowsDefaultUserId = 1000;

    // The in-container id that a rootless daemon maps to the invoking user — i.e. to the engine's own host account.
    // See ResolveIdentity for the measurement; this constant is 0 because of that mapping, not because root is wanted.
    private const int RootlessMappedUserId = 0;

    // Prefix for the file the create-time mapping probe writes. Dot-leading so an ordinary `ls` in the workspace does
    // not show it, and removed host-side immediately afterwards either way.
    private const string WorkspaceProbePrefix = ".xe-sandbox-mapping-probe-";

    private readonly IDockerRuntimeClientFactory _clientFactory;
    private readonly string _installId;
    private readonly ILogger<DockerSandboxRuntimeProvider> _logger;
    private readonly IOptionsMonitor<ContainerSandboxOptions> _options;
    private readonly ConcurrentDictionary<string, SandboxState> _sandboxes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public DockerSandboxRuntimeProvider(IOptionsMonitor<ContainerSandboxOptions> options,
        IDockerRuntimeClientFactory clientFactory,
        INodeDataDirectory nodeDataDirectory,
        TimeProvider timeProvider,
        ILogger<DockerSandboxRuntimeProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(nodeDataDirectory);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _installId = BuildInstallId(nodeDataDirectory.Root);
    }

    public string ProviderName => Name;

    /// <summary>
    ///     Advertises only what this provider verifies on a real container, which is not the same as what it passes
    ///     to the daemon.
    /// </summary>
    /// <remarks>
    ///     <see cref="SandboxProviderCapabilities.SupportsCopyInto" /> is served through the workspace bind mount and
    ///     never through Docker's archive endpoint, which a read-only-rootfs container refuses outright, under the
    ///     containment and symlink guards <c>DockerWorkspaceHostFiles</c> applies. That each side can read what the
    ///     other wrote is proved per sandbox by <see cref="CreateOrAttachAsync" />'s probe file rather than claimed
    ///     once. Both rules are recorded in <c>docs/adr/0004-…</c>.
    /// </remarks>
    public SandboxProviderCapabilities Capabilities =>
        SandboxProviderCapabilities.SupportsCopyOut
        | SandboxProviderCapabilities.SupportsCopyInto
        | SandboxProviderCapabilities.SupportsReadOnlyMounts
        | SandboxProviderCapabilities.SupportsNetworkPolicy
        | SandboxProviderCapabilities.SupportsResourceLimits
        | SandboxProviderCapabilities.SupportsCommandCancellation
        | SandboxProviderCapabilities.SupportsAttach
        | SandboxProviderCapabilities.SupportsKill
        | SandboxProviderCapabilities.SupportsTrustedHostWorkspace
        // The reason this backend exists (ADR 0004 Context): a confinement mechanism restricts what a process may
        // touch while it still runs against the HOST's SDKs. This one does not, nor can it offer the host toolchain.
        | SandboxProviderCapabilities.SuppliesImageToolchain
        // The host filesystem is absent by construction and every create reads the settings back, so an unverified
        // container never reaches a caller. SupportsFilesystemIsolation is deliberately absent; see RejectUnservable.
        | SandboxProviderCapabilities.SupportsHostFilesystemBoundary;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        foreach (var state in _sandboxes.Values)
        {
            await TerminateAsync(state, CancellationToken.None);
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

        await _sync.WaitAsync(cancellationToken);
        try
        {
            var sandboxId = BuildSandboxId(request.AttachKey);
            if (_sandboxes.TryGetValue(sandboxId, out var existing))
            {
                EnsureCompatibleWorkspaceBinding(existing, request.TrustedHostWorkspace);
                EnsureCompatibleMounts(existing, request, options);
                return existing.Handle;
            }

            await EvictOwnerConflictsAsync(request.AttachKey, cancellationToken);
            return await CreateVerifiedAsync(request, options, sandboxId, cancellationToken);
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
                    // MAPPED, not forwarded: the caller's working directory names the
                    // sandbox namespace, whose root is the workspace, not the container's.
                    WorkingDirectory = request.WorkingDirectory is null
                        ? state.WorkspaceMountTarget
                        : DockerSandboxPaths.ResolveContainerPath(state.WorkspaceMountTarget, request.WorkingDirectory),
                    Environment = request.Environment,
                    StandardInput = request.StandardInput,
                    MaxCapturedBytes = DefaultMaxCapturedOutputBytes
                },
                execution.Token);

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

    /// <summary>
    ///     Writes a host file into the sandbox through the workspace bind mount rather than through Docker's archive
    ///     endpoint, which a read-only-rootfs container refuses outright (see <see cref="Capabilities" />).
    /// </summary>
    /// <remarks>
    ///     The destination maps to the mount's HOST path, not its container path — the write happens on this side of
    ///     the mount — under the guards the process provider applies to its jail: containment under the workspace
    ///     root, rejection of any symlinked component, and an <c>O_NOFOLLOW</c> create. A command in the container
    ///     can plant a symlink the host then resolves, so an unguarded write lets the sandbox choose where the
    ///     engine writes.
    /// </remarks>
    public async Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        var content = await File.ReadAllBytesAsync(request.SourcePath, cancellationToken);

        await DockerWorkspaceHostFiles.WriteAsync(state.WorkspaceRoot,
            state.WorkspaceMountTarget,
            request.DestinationPath,
            content,
            cancellationToken);
    }

    public async Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);

        return await ReadFileAsync(handle, sandboxPath, DefaultMaxCapturedOutputBytes, cancellationToken);
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
            cancellationToken);

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

        // A bounded in-container read plus a host write, never the Docker archive API: on a rootless daemon that
        // endpoint fails with a remount-ro error for any path under a bind mount, where every artifact lives.
        var content = await ReadFileAsync(handle, request.SourcePath, DefaultMaxCapturedOutputBytes, cancellationToken);

        var directory = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(request.DestinationPath, content, cancellationToken);
    }

    public async Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        if (state.InFlight.TryGetValue(executionId, out var execution))
        {
            await execution.CancelAsync();
        }
    }

    public async Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (_sandboxes.TryRemove(handle.SandboxId, out var state))
        {
            await TerminateAsync(state, cancellationToken);
        }
    }

    /// <summary>Resolve the in-container UID/GID for the daemon that is about to run the container.</summary>
    /// <remarks>
    ///     The container must run as the identity that maps to the engine's own host UID, and that identity must not
    ///     map to host root; the two daemon modes answer that with opposite numbers, so a rootless daemon's container
    ///     UID 0 is the correct answer and a rootful daemon's is the engine's own effective ids. An explicit
    ///     operator-configured id wins over both, a daemon being free to map identities in a way neither rule
    ///     describes. The measurements are in <c>docs/adr/0004-…</c> ("The identity rule").
    /// </remarks>
    internal static ResolvedContainerIdentity ResolveIdentity(ContainerSandboxOptions options,
        bool daemonIsRootless,
        Func<int> userIdReader,
        Func<int> groupIdReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(userIdReader);
        ArgumentNullException.ThrowIfNull(groupIdReader);

        var userId = options.UserId ?? (daemonIsRootless ? RootlessMappedUserId : userIdReader());
        var groupId = options.GroupId ?? (daemonIsRootless ? RootlessMappedUserId : groupIdReader());

        if (userId < 0 || groupId < 0)
        {
            throw new SandboxCapabilityNotSupportedException($"The docker sandbox provider refuses to create a container as uid {userId}, gid {groupId}: neither may be negative.");
        }

        if ((userId == 0 || groupId == 0) && !daemonIsRootless)
        {
            throw new SandboxCapabilityNotSupportedException($"The docker sandbox provider refuses to create a container as uid {userId}, gid {groupId} against a daemon that "
                                                             + "does not report itself rootless. On a rootful daemon an in-container id maps straight through, so 0 is host "
                                                             + "root — which the §3.8 hardening contract forbids. (0 is accepted only against a daemon that reports itself "
                                                             + "rootless, where it maps to the invoking user's own unprivileged account. That is a description of how the "
                                                             + "two daemon modes differ, not a recommendation to switch: this product neither requires nor supplies rootless "
                                                             + "Docker.) Set "
                                                             + $"'{ContainerSandboxOptions.SectionName}:UserId' and ':GroupId' to the ids that own this node's workspace.");
        }

        return new ResolvedContainerIdentity
        {
            UserId = userId,
            GroupId = groupId
        };
    }

    private static ResolvedContainerIdentity ResolveIdentity(ContainerSandboxOptions options, bool daemonIsRootless)
    {
        return ResolveIdentity(options,
            daemonIsRootless,
            static () => OperatingSystem.IsWindows() ? WindowsDefaultUserId : (int)GetEffectiveUserId(),
            static () => OperatingSystem.IsWindows() ? WindowsDefaultUserId : (int)GetEffectiveGroupId());
    }

    /// <summary>
    ///     Decides whether the workspace mount behaves as both sides need, from the evidence of one probe file the
    ///     container created; null when the mapping is sound, or the reason it is not.
    /// </summary>
    /// <remarks>
    ///     A pure function because it is the half that has to be tested against mappings this machine cannot
    ///     produce. It exists because <c>inspect</c> echoes back the UID it was asked for and cannot say what that
    ///     UID maps to, so a perfect read-back is compatible with a container that cannot write a byte. One probe
    ///     settles writability, the identity mapping and the engine's own access, without trusting the
    ///     <c>rootless</c> label.
    /// </remarks>
    internal static string? DescribeWorkspaceMappingFailure(bool containerWroteTheProbe,
        bool probeVisibleOnHost,
        uint? engineUserId,
        uint? probeOwnerUserId)
    {
        if (!containerWroteTheProbe)
        {
            return "the container could not create a file in its own workspace mount. Under a rootless daemon this is what a "
                   + "conventional non-root UID looks like: container uid N>0 maps to the subordinate range, which does not own "
                   + "the engine-generated workspace.";
        }

        if (!probeVisibleOnHost)
        {
            return "the file the container created in its workspace mount is not present on the host path the engine bound "
                   + "there, so the two sides are not looking at the same bytes.";
        }

        if (engineUserId is null)
        {
            // Non-Linux engine host: there is no host UID to compare against, and the write-through checks above have
            // already established the property that matters. Nothing further is claimed rather than guessed.
            return null;
        }

        if (probeOwnerUserId is null)
        {
            return "the owner of the file the container created could not be read on the host, so the engine cannot confirm it "
                   + "owns what the container writes. Refused rather than assumed.";
        }

        return probeOwnerUserId == engineUserId
            ? null
            : $"the container writes into the workspace as host uid {probeOwnerUserId}, but this engine runs as uid "
              + $"{engineUserId}, so neither side can modify the other's files. Set "
              + $"'{ContainerSandboxOptions.SectionName}:UserId' and ':GroupId' to the in-container ids that map to uid "
              + $"{engineUserId} on this daemon.";
    }

    // DllImport rather than the source-generated LibraryImport, as ProcessSandboxRuntimeProvider does: the generated
    // form needs AllowUnsafeBlocks project-wide, neither call takes a buffer, and neither can fail.
    [DllImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getegid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveGroupId();

    /// <summary>Reject, up front, every request this provider cannot serve exactly as asked.</summary>
    /// <remarks>
    ///     Rejecting before creating is not merely tidier: a request for an un-isolated network that got as far as a
    ///     created container would leave the caller reasoning about a container that should never have existed.
    /// </remarks>
    private static void RejectUnservableRequest(SandboxCreateRequest request, ContainerSandboxOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Image))
        {
            throw new SandboxCapabilityNotSupportedException("The docker sandbox provider has no approved container image configured. Set "
                                                             + $"'{ContainerSandboxOptions.SectionName}:Image' to a digest-pinned reference.");
        }

        if (request.TrustedHostWorkspace is null)
        {
            // This provider creates exactly one engine-generated mount, and it is the workspace. Without it the container
            // would have nothing to act on, and a container with no workspace is not a sandbox, it is an idle process.
            throw new SandboxCapabilityNotSupportedException("The docker sandbox provider requires an engine-managed trusted host workspace on the create request.");
        }

        // A container's filesystem boundary is NOT the one SandboxIsolationMode.Filesystem names — that contract is
        // the bubblewrap chain's — so serving it would hand the caller a different boundary than the one it asked.
        if (request.Isolation == SandboxIsolationMode.Filesystem)
        {
            throw new SandboxCapabilityNotSupportedException("The docker sandbox provider does not implement SandboxIsolationMode.Filesystem; its container boundary is a different contract "
                                                             + "(no ReadOnlyTrees, no synthetic /etc, no jail-backed /tmp). Gate the request on SupportsFilesystemIsolation.");
        }

        // `None` and `Unrestricted` have mechanisms and are served exactly as asked; `Restricted` does not and is
        // rejected rather than downgraded, an allow-list served as an open bridge being the weakening this prevents.
        _ = DockerSandboxHardening.ResolveNetworkMode(request.NetworkPolicy);

        ValidateMountTargets(request, options, Path.GetFullPath(request.TrustedHostWorkspace.RootPath));
    }

    /// <summary>
    ///     Rejects, before anything is created, every engine-generated mount this provider could not place exactly as
    ///     asked.
    /// </summary>
    /// <remarks>
    ///     The overlap sweep is N-way and shared with startup validation
    ///     (<see cref="ContainerSandboxOptionsValidator.FindOverlap" />), because a mount placed at an ancestor of
    ///     another silently hides everything the descendant was meant to expose — after which the daemon's read-back
    ///     still agrees, having been asked for exactly that. One nesting is legitimate: a FILE mount over a directory
    ///     mount, which is how <c>.git/config</c> is made read-only without freezing the work tree.
    /// </remarks>
    private static void ValidateMountTargets(SandboxCreateRequest request, ContainerSandboxOptions options, string workspaceRoot)
    {
        var strict = new List<ContainerMountTarget>
        {
            new()
            {
                Name = nameof(ContainerSandboxOptions.WorkspaceMountTarget),
                Path = options.WorkspaceMountTarget
            },
            new()
            {
                Name = nameof(ContainerSandboxOptions.ScratchMountTarget),
                Path = options.ScratchMountTarget
            },
            new()
            {
                Name = nameof(ContainerSandboxOptions.TempMountTarget),
                Path = options.TempMountTarget
            }
        };
        var overlays = new List<string>();

        foreach (var mount in request.Mounts ?? [])
        {
            ValidateMountTarget(mount);
            var isFile = File.Exists(mount.HostPath);
            if (!isFile && !Directory.Exists(mount.HostPath))
            {
                // A bind source the daemon has never seen is created BY the daemon with its own ownership, so the
                // container cannot write its HOME. Refused here, or it surfaces as a permission error inside a build.
                throw new SandboxCapabilityNotSupportedException($"The engine-generated sandbox mount source '{mount.HostPath}' does not exist. The engine must create it before the "
                                                                 + "sandbox: a bind source the daemon has to invent is created with the daemon's own ownership, not the engine's.");
            }

            // The RESOLVED target, not the requested one. A mount inside the trusted workspace is placed by derivation
            // (see ResolveMountTarget), so sweeping the requested string would sweep a path that is never applied.
            var target = ResolveMountTarget(mount, options, workspaceRoot);
            if (isFile)
            {
                overlays.Add(target);
            }
            else
            {
                strict.Add(new ContainerMountTarget
                {
                    Name = "mount " + target,
                    Path = target
                });
            }
        }

        if (ContainerSandboxOptionsValidator.FindOverlap(strict) is { } collision)
        {
            throw new SandboxCapabilityNotSupportedException($"The engine-generated sandbox mounts '{collision.First.Name}' ('{collision.First.Path}') and '{collision.Second.Name}' "
                                                             + $"('{collision.Second.Path}') overlap. One would shadow the other, and the daemon's read-back would still agree "
                                                             + "because it applied exactly what it was asked for.");
        }

        // A file overlay is admitted above, but only as an overlay: it must still not land exactly on top of a
        // directory mount target, which would replace the whole mount with a single file.
        var replaced = overlays.FirstOrDefault(overlay =>
            strict.Any(target => string.Equals(target.Path?.TrimEnd('/'), overlay.TrimEnd('/'), StringComparison.Ordinal)));
        if (replaced is not null)
        {
            throw new SandboxCapabilityNotSupportedException($"The engine-generated file mount '{replaced}' lands exactly on a directory mount target and would replace it.");
        }
    }

    /// <summary>
    ///     Container paths are POSIX whatever the engine host is (a native Windows engine may drive a Linux
    ///     container), so this validates the string rather than asking <see cref="Path" />, whose rooting and separator
    ///     rules would answer for the wrong operating system.
    /// </summary>
    private static void ValidateMountTarget(SandboxMount mount)
    {
        if (string.IsNullOrWhiteSpace(mount.HostPath) || string.IsNullOrWhiteSpace(mount.SandboxPath))
        {
            throw new SandboxCapabilityNotSupportedException("An engine-generated sandbox mount must name both a host path and an in-container target.");
        }

        if (!mount.SandboxPath.StartsWith('/')
            || mount.SandboxPath.Contains("..", StringComparison.Ordinal)
            || mount.SandboxPath.TrimEnd('/').Length == 0)
        {
            throw new SandboxCapabilityNotSupportedException($"The engine-generated sandbox mount target '{mount.SandboxPath}' must be an absolute in-container path below '/', "
                                                             + "with no '..' segment.");
        }
    }

    private async Task<SandboxHandle> CreateVerifiedAsync(SandboxCreateRequest request,
        ContainerSandboxOptions options,
        string sandboxId,
        CancellationToken cancellationToken)
    {
        var workspaceRoot = Path.GetFullPath(request.TrustedHostWorkspace!.RootPath);
        Directory.CreateDirectory(workspaceRoot);

        var endpoint = DockerDaemonEndpointResolver.Resolve(options);
        var client = _clientFactory.Create(endpoint);
        string? containerId = null;

        try
        {
            // Probed BEFORE the identity is resolved, because the identity depends on it: which in-container UID maps
            // to this engine's host UID is a property of the daemon, not of the configuration.
            var daemon = await client.ProbeAsync(cancellationToken);
            var identity = ResolveIdentity(options, daemon.IsRootless);

            var bindMounts = BuildBindMounts(request, options, workspaceRoot);
            var specification = DockerSandboxHardening.BuildSpecification(options,
                identity,
                "xe-dev-" + sandboxId,
                sandboxId,
                _installId,
                bindMounts,
                // Honored, not ignored: this provider advertises SupportsResourceLimits, so the caller's ceiling is
                // the one applied, and the read-back below checks the caller's numbers, not the engine's defaults.
                request.ResourceLimits,
                request.NetworkPolicy);

            containerId = await client.CreateContainerAsync(specification, cancellationToken);
            await client.StartContainerAsync(containerId, cancellationToken);

            var observed = await client.InspectContainerAsync(containerId, cancellationToken);
            var violations = DockerSandboxHardening.FindViolations(specification, observed, daemon.IsRootless);
            if (violations.Count > 0)
            {
                // The whole point of the read-back. The container exists and is running, and it is still refused,
                // because a container that is not the container we asked for is not usable evidence of anything.
                _logger.LogError("Refusing container {ContainerId}: {ViolationCount} hardening guarantee(s) could not be verified.",
                    containerId,
                    violations.Count);

                throw new SandboxCapabilityNotSupportedException("The docker sandbox provider created a container whose isolation settings could not be verified against the "
                                                                 + "daemon's own read-back, so it was removed rather than used. Unverified guarantees: "
                                                                 + string.Join(" ", violations));
            }

            await VerifyWorkspaceMappingAsync(client, containerId, workspaceRoot, options.WorkspaceMountTarget, identity, cancellationToken);

            var handle = new SandboxHandle
            {
                ProviderName = Name,
                SandboxId = sandboxId,
                AttachKey = request.AttachKey,
                CreatedAt = _timeProvider.GetUtcNow(),
                ManifestVersion = request.AttachKey.ManifestVersion,
                // Read off the SPECIFICATION, which is the same list the read-back was verified against — so what the
                // handle reports is what the daemon confirmed it applied, not what the caller asked for.
                Mounts =
                [
                    .. bindMounts.Select(static mount => new SandboxMountBinding
                    {
                        HostPath = mount.HostPath,
                        SandboxPath = mount.ContainerPath,
                        ReadOnly = mount.ReadOnly
                    })
                ],
                // The CONTAINER path a command with no working directory runs in — the same value ExecuteAsync falls
                // back to. It names nothing on the host, which is exactly why the handle reports it as a sandbox path.
                WorkingRoot = options.WorkspaceMountTarget
            };

            _sandboxes[sandboxId] = new SandboxState(handle, client, containerId, workspaceRoot, options.WorkspaceMountTarget);
            _logger.LogInformation("Created verified sandbox container {ContainerId} for sandbox {SandboxId}.", containerId, sandboxId);
            return handle;
        }
        catch
        {
            if (containerId is not null)
            {
                await SafeRemoveAsync(client, containerId);
            }

            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    ///     Turns the neutral mount contract into Docker bind mounts: the workspace first, then every
    ///     engine-generated mount at the target it asked for.
    /// </summary>
    /// <remarks>
    ///     This is the SAME list handed to <see cref="DockerSandboxHardening.BuildSpecification" /> and therefore the
    ///     one the read-back is checked against — every requested mount present with the propagation and read-only
    ///     flag it asked for, and no mount the engine did not request. Composing a second list here would route these
    ///     mounts around that check while leaving it looking intact.
    /// </remarks>
    private static IReadOnlyList<DockerBindMount> BuildBindMounts(SandboxCreateRequest request,
        ContainerSandboxOptions options,
        string workspaceRoot)
    {
        var mounts = new List<DockerBindMount>
        {
            new()
            {
                HostPath = workspaceRoot,
                ContainerPath = options.WorkspaceMountTarget,
                ReadOnly = false,
                Propagation = DockerSandboxHardening.PrivateMountPropagation
            }
        };

        mounts.AddRange((request.Mounts ?? []).Select(mount => new DockerBindMount
        {
            HostPath = Path.GetFullPath(mount.HostPath),
            ContainerPath = ResolveMountTarget(mount, options, workspaceRoot),
            ReadOnly = mount.ReadOnly,
            Propagation = DockerSandboxHardening.PrivateMountPropagation
        }));

        return mounts;
    }

    /// <summary>Where one engine-generated mount lands inside the container.</summary>
    /// <remarks>
    ///     A host path INSIDE the trusted workspace is derived from the workspace mount target and its own relative
    ///     path, ignoring the requested <see cref="SandboxMount.SandboxPath" />, so the engine can ask for a nested
    ///     mount — the read-only <c>.git/config</c> — without knowing what the workspace is called inside a
    ///     container, which is the Docker-shaped knowledge the neutral contract forbids it. Everything else lands at
    ///     the caller's target, a per-task HOME or package cache having to stay outside the work tree.
    /// </remarks>
    private static string ResolveMountTarget(SandboxMount mount, ContainerSandboxOptions options, string workspaceRoot)
    {
        // The one shape derivation cannot express: an engine-generated source outside the workspace with a target
        // inside it. ResolveContainerPath still rejects every '..' escape, so no target can leave the mount.
        if (mount.TargetIsWorkspaceRelative)
        {
            return DockerSandboxPaths.ResolveContainerPath(options.WorkspaceMountTarget, mount.SandboxPath);
        }

        var hostPath = Path.GetFullPath(mount.HostPath);
        var workspacePrefix = Path.TrimEndingDirectorySeparator(workspaceRoot) + Path.DirectorySeparatorChar;
        if (!hostPath.StartsWith(workspacePrefix, StringComparison.Ordinal))
        {
            return DockerSandboxPaths.NormalizePosix(mount.SandboxPath);
        }

        var relative = hostPath[workspacePrefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
        return DockerSandboxPaths.ResolveContainerPath(options.WorkspaceMountTarget, relative);
    }

    /// <summary>
    ///     Proves the workspace mount is usable in both directions, by having the container create a probe file and
    ///     reading it back from the host; throws <see cref="SandboxCapabilityNotSupportedException" /> when it is not.
    /// </summary>
    /// <remarks>
    ///     This is the half of the hardening contract's user check the daemon cannot perform: an inspect echoes back
    ///     the UID that was asked for and cannot say what it maps to, so under a rootless daemon a conformant
    ///     read-back is compatible with a container that cannot write a byte into its own workspace. The caller's
    ///     <c>catch</c> removes the container.
    /// </remarks>
    private async Task VerifyWorkspaceMappingAsync(IDockerRuntimeClient client,
        string containerId,
        string workspaceRoot,
        string workspaceMountTarget,
        ResolvedContainerIdentity identity,
        CancellationToken cancellationToken)
    {
        var probeName = WorkspaceProbePrefix + Guid.NewGuid().ToString("N");
        var hostProbePath = Path.Combine(workspaceRoot, probeName);

        try
        {
            // `touch` through the exec API rather than a shell line: no quoting, and therefore nothing for a mount
            // target containing a space or a quote to do.
            var outcome = await client.ExecuteAsync(containerId,
                new DockerExecutionRequest
                {
                    Executable = "touch",
                    Arguments = [DockerSandboxPaths.ResolveContainerPath(workspaceMountTarget, probeName)],
                    MaxCapturedBytes = ProbeCapturedOutputBytes
                },
                cancellationToken);

            var engineUserId = OperatingSystem.IsLinux() ? GetEffectiveUserId() : (uint?)null;
            var failure = DescribeWorkspaceMappingFailure(outcome.ExitCode == 0,
                File.Exists(hostProbePath),
                engineUserId,
                File.Exists(hostProbePath) ? DockerWorkspaceHostFiles.TryReadOwnerUserId(hostProbePath) : null);

            if (failure is not null)
            {
                _logger.LogError("Refusing container {ContainerId}: its workspace mount does not map to this engine ({Failure})",
                    containerId,
                    failure);

                throw new SandboxCapabilityNotSupportedException($"The docker sandbox provider created a container as uid {identity.UserSpecification}, but {failure} The container "
                                                                 + "was removed rather than used: a sandbox whose workspace the engine and the container cannot both write is not "
                                                                 + "one Development Mode can run in, and the daemon's own read-back cannot detect this — it reports the id that "
                                                                 + "was asked for, never what that id maps to.");
            }
        }
        finally
        {
            SafeDeleteProbe(hostProbePath);
        }
    }

    private void SafeDeleteProbe(string hostProbePath)
    {
        try
        {
            File.Delete(hostProbePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A probe file left behind is untidy, not unsafe, and must never mask the verdict that produced it.
            _logger.LogWarning(exception, "Failed to remove the workspace mapping probe at {ProbePath}.", hostProbePath);
        }
    }

    private async Task SafeRemoveAsync(IDockerRuntimeClient client, string containerId)
    {
        try
        {
            await client.RemoveContainerAsync(containerId, CancellationToken.None);
        }
        catch (DockerRuntimeException exception)
        {
            // A container that could not be removed is a leak, not a reason to swallow the original rejection. Logged
            // loudly; the startup reaper is the thing that finally collects it, and it is later work.
            _logger.LogError(exception, "Failed to remove container {ContainerId} after a fail-closed create.", containerId);
        }
    }

    /// <summary>
    ///     A container's mounts are fixed at creation, so an attach asking for a different set is refused.
    /// </summary>
    /// <remarks>
    ///     Refused rather than ignored: silently returning the old container would hand the caller a handle whose
    ///     reported mapping is right and whose content is a set of mounts it did not ask for.
    /// </remarks>
    private static void EnsureCompatibleMounts(SandboxState state, SandboxCreateRequest request, ContainerSandboxOptions options)
    {
        var requested = BuildBindMounts(request, options, state.WorkspaceRoot)
                        .Select(static mount => (mount.HostPath, mount.ContainerPath, mount.ReadOnly))
                        .ToArray();
        var applied = state.Handle.Mounts.Select(static mount => (mount.HostPath, SandboxPath: mount.SandboxPath, mount.ReadOnly)).ToArray();

        if (!requested.SequenceEqual(applied))
        {
            throw new SandboxCapabilityNotSupportedException("An existing sandbox for this attach key carries a different engine-generated mount set. A container's mounts are fixed "
                                                             + "at creation, so kill it before rebinding.");
        }
    }

    private static void EnsureCompatibleWorkspaceBinding(SandboxState state, SandboxTrustedHostWorkspace? workspace)
    {
        var requested = workspace is null ? null : Path.GetFullPath(workspace.RootPath);
        if (!string.Equals(state.WorkspaceRoot, requested, StringComparison.Ordinal))
        {
            throw new SandboxCapabilityNotSupportedException("An existing sandbox for this attach key is bound to a different trusted host workspace. Kill it before rebinding.");
        }
    }

    /// <summary>
    ///     An owner change on the same node forbids reuse: kill and remove any sandbox keyed to that node under a
    ///     different owner before creating the new one.
    /// </summary>
    /// <remarks>
    ///     Awaited rather than blocked on, because this runs while the create semaphore is held and a
    ///     sync-over-async wait there would turn a slow daemon into a deadlocked provider.
    /// </remarks>
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
                await TerminateAsync(state, cancellationToken);
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
            await execution.CancelAsync();
        }

        state.InFlight.Clear();

        try
        {
            await state.Client.RemoveContainerAsync(state.ContainerId, cancellationToken);
        }
        catch (DockerRuntimeException exception)
        {
            _logger.LogError(exception, "Failed to remove sandbox container {ContainerId}.", state.ContainerId);
        }
        finally
        {
            await state.Client.DisposeAsync();
        }
    }

    /// <summary>
    ///     Removes every container this INSTALLATION created that no live sandbox references, best-effort and
    ///     idempotent.
    /// </summary>
    /// <remarks>
    ///     It collects the leak the in-memory registry cannot: a hard host kill runs neither
    ///     <see cref="DisposeAsync" /> nor <see cref="KillAsync" />, and <c>SandboxOrphanReaper</c> knows nothing
    ///     about containers. A leaked one still owns its name, which the same attach key collides with forever. The
    ///     filter is both <see cref="DockerSandboxHardening.OwnerLabel" /> and
    ///     <see cref="DockerSandboxHardening.InstallLabel" />, so another installation's containers never qualify.
    /// </remarks>
    /// <returns>How many containers were removed.</returns>
    internal async Task<int> SweepOrphanedContainersAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = DockerDaemonEndpointResolver.Resolve(_options.CurrentValue);
        var client = _clientFactory.Create(endpoint);

        try
        {
            var live = _sandboxes.Values.Select(static state => state.ContainerId).ToHashSet(StringComparer.Ordinal);
            var owned = await client.ListContainersAsync(BuildOwnershipFilter(), cancellationToken);
            var removed = 0;

            foreach (var containerId in owned)
            {
                if (live.Contains(containerId))
                {
                    // This process is using it. Only reachable if the sweep is ever run after start; at startup the
                    // registry is empty by construction.
                    continue;
                }

                try
                {
                    await client.RemoveContainerAsync(containerId, cancellationToken);
                    removed++;
                    _logger.LogInformation("Removed orphaned Development Mode container {ContainerId} left by a previous run.", containerId);
                }
                catch (DockerRuntimeException exception)
                {
                    // One container that will not go is not a reason to leave the rest. Logged at error because a
                    // container the engine cannot remove is a leak an operator has to clear by hand.
                    _logger.LogError(exception, "Failed to remove orphaned Development Mode container {ContainerId}.", containerId);
                }
            }

            return removed;
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>The label set that identifies a container as belonging to THIS installation's Development Mode.</summary>
    private IReadOnlyDictionary<string, string> BuildOwnershipFilter()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DockerSandboxHardening.OwnerLabel] = DockerSandboxHardening.OwnerLabelValue,
            [DockerSandboxHardening.InstallLabel] = _installId
        };
    }

    /// <summary>
    ///     The id that distinguishes this engine installation's containers from another's on the same daemon.
    /// </summary>
    /// <remarks>
    ///     Derived from the node data directory, the only identity available at startup that is both stable across
    ///     restarts and different per installation, and hashed for the reason <see cref="BuildSandboxId" /> hashes:
    ///     the path routinely contains the operator's account name and a container label is readable by anyone who
    ///     can list containers on that daemon. Two installations sharing one node data directory would share an id
    ///     and sweep each other's containers; that configuration is excluded elsewhere, not defended against here.
    /// </remarks>
    internal static string BuildInstallId(string nodeDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeDataRoot);

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(nodeDataRoot));
        if (OperatingSystem.IsWindows())
        {
            // A Windows path is case-insensitive, so two spellings of one directory must not produce two ids. Upper
            // rather than lower only because CA1308 says so; the value is hashed either way and never displayed.
            canonical = canonical.ToUpperInvariant();
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
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
