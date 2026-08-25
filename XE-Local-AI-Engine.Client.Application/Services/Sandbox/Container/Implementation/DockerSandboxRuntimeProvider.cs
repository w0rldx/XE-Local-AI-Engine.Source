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
///     Docker hardening contract and <em>verified</em> against the daemon's own read-back before the handle is returned.
///     Permitted for Development Mode build/test/lint execution only, per ADR 0004.
///     <para>
///         Security posture, stated per mechanism rather than as one claim. Unconditionally, and verified against the
///         daemon's read-back: the container gets its own filesystem, PID, IPC and UTS namespaces, every capability
///         dropped, no-new-privileges, a read-only root filesystem, no devices, and enforced CPU/memory/PID ceilings.
///         <b>Egress is confined only when the caller asks for it.</b> <see cref="SandboxNetworkPolicy.None" /> gets an
///         empty network namespace; <see cref="SandboxNetworkPolicy.Unrestricted" /> gets Docker's default bridge —
///         still a private namespace with no host interface, but with NAT egress — and Development Mode requests that
///         today because its <c>dotnet restore</c> needs the network until package-proxy machinery exists.
///         <see cref="SandboxNetworkPolicy.Restricted" /> has no mechanism here and stays fail-closed rejected. So do
///         not read "container" as "offline": whichever policy is in force is the one the caller chose, and it is the
///         one verified.
///     </para>
///     <para>
///         What none of this is, is a replacement for the MXC seam: ADR 0004 records Docker as an interim backend
///         behind <see cref="ISandboxRuntimeProvider" />, not as the end of that seam, and on Linux the daemon socket
///         this provider talks to is root-equivalent.
///     </para>
///     <para>
///         Fail-closed everywhere. If any single hardening-contract guarantee cannot be read back off the created container, the
///         container is removed and the create is rejected with <see cref="SandboxCapabilityNotSupportedException" />.
///         There is no path through this class that returns a handle to a container it could not verify.
///     </para>
///     <para>
///         Scope. This provider is registered but is still NOT wired into Development Mode execution — that switch is
///         per-feature provider selection, and <c>SandboxProviderSelector</c> remains untouched. What exists here
///         now is the workspace bind mount PLUS the engine-generated mounts of the neutral mount broker, which
///         is what makes a container able to serve a build at all: a read-only rootfs with no HOME, temp or package
///         cache cannot run <c>dotnet restore</c>. The dependency-manifest rejection, lifecycle ownership and the
///         startup reaper are still later work.
///     </para>
/// </summary>
// Implements the Development role ONLY, and that omission is load-bearing rather than an oversight: ADR 0004 permits
// Docker for Development Mode build/test/lint execution only, so this provider deliberately does NOT implement
// IAgentSandboxRuntimeProvider. Registering it for AgentHome or Coder is therefore a COMPILE ERROR, not something a
// reviewer has to notice — which is what keeps a container requirement from spreading to features deliberately scoped out of it.
public sealed class DockerSandboxRuntimeProvider : IDevelopmentSandboxRuntimeProvider, IAsyncDisposable
{
    /// <summary>The provider name this registers under for configuration-bound selection.</summary>
    public const string Name = "docker";

    private const int DefaultMaxCapturedOutputBytes = 4 * 1024 * 1024;

    // The mapping probe runs `touch`, whose entire useful output is an error line. A small ceiling keeps a
    // misbehaving image from turning a create-time check into a multi-megabyte capture.
    private const int ProbeCapturedOutputBytes = 4 * 1024;

    // On Windows the engine is a native Windows process while the container is Linux, so the host's own account
    // identifiers do not name anything inside it. 1000 is the conventional first non-root Linux account and is only a
    // default: an operator whose image expects another id sets UserId/GroupId explicitly.
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
    ///     Advertises only what this provider verifies on a real container, which is not the same as what it passes to
    ///     the daemon.
    ///     <para>
    ///         <see cref="SandboxProviderCapabilities.SupportsCopyInto" /> is served, but not through Docker's archive
    ///         endpoint: Docker refuses <c>PUT /containers/{id}/archive</c> outright against a container with a
    ///         read-only root filesystem — measured against Engine 29.6.1, which answers
    ///         <c>400 container rootfs is marked read-only</c> regardless of the destination path, including a writable
    ///         <c>tmpfs</c> — and the Docker hardening contract makes that root filesystem non-negotiable. The workspace bind mount is the same
    ///         bytes on both sides, so the write goes to the host path backing the destination, under the containment
    ///         and symlink guards <c>DockerWorkspaceHostFiles</c> applies. That the engine and the container can each
    ///         read what the other wrote is not assumed either: <see cref="CreateOrAttachAsync" /> proves it with a
    ///         probe file before it returns a handle, so this capability is verified per sandbox rather than claimed
    ///         once.
    ///     </para>
    /// </summary>
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
        // touch, but the process still runs against the HOST's SDKs. This one does not — and it cannot offer the
        // host's toolchain either, which is why the two flags are exclusive rather than additive.
        | SandboxProviderCapabilities.SuppliesImageToolchain
        // The host filesystem is absent from this sandbox by construction — read-only rootfs, engine-generated mounts
        // only, no host namespaces — and every create reads those settings back and fails closed on any mismatch, so a
        // container that does not have the property is never handed to a caller. Note what is deliberately NOT here:
        // SupportsFilesystemIsolation, which means "serves SandboxIsolationMode.Filesystem", a different contract this
        // provider still refuses below.
        | SandboxProviderCapabilities.SupportsHostFilesystemBoundary;

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
                EnsureCompatibleMounts(existing, request, options);
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

    /// <summary>
    ///     Writes a host file into the sandbox through the workspace bind mount rather than through Docker's archive
    ///     endpoint, which a read-only-rootfs container refuses outright (see <see cref="Capabilities" />).
    ///     <para>
    ///         The destination is mapped to the mount's HOST path, not its container path — the whole point is that the
    ///         write happens on this side of the mount — and it is then subjected to the same guards the process
    ///         provider applies to its jail: containment under the workspace root, rejection of any symlinked component,
    ///         and an <c>O_NOFOLLOW</c> create. Those are not ceremony here. A command running in the container can
    ///         plant a symlink in the workspace, and it is the host that resolves it, so an unguarded write would let
    ///         the sandbox choose where the engine writes.
    ///     </para>
    /// </summary>
    public async Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        var content = await File.ReadAllBytesAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);

        await DockerWorkspaceHostFiles.WriteAsync(state.WorkspaceRoot,
                                          state.WorkspaceMountTarget,
                                          request.DestinationPath,
                                          content,
                                          cancellationToken)
                                      .ConfigureAwait(false);
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
    ///     Resolve the in-container UID/GID for the daemon that is about to run the container.
    ///     <para>
    ///         The rule is <em>the container must run as the identity that maps to the engine's own host UID, and that
    ///         identity must not map to host root</em>, and the two daemon modes answer it with opposite numbers. On a
    ///         rootful daemon an in-container UID maps straight through, so the answer is the engine's own effective
    ///         ids and zero would be host root. On a rootless daemon container UID 0 <b>is</b> the invoking user —
    ///         measured on Engine 29.6.1 rootless with <c>/etc/subuid</c> = <c>…:100000:65536</c>, a container run as
    ///         <c>1000:1000</c> could not create a file in the engine-generated workspace mount at all
    ///         (<c>Permission denied</c>, because container 1000 is host 100999), while one run as <c>0:0</c> wrote
    ///         files that landed host-side owned by uid 1000, the engine's own account. Refusing zero there would
    ///         refuse the only identity that works.
    ///     </para>
    ///     <para>
    ///         "Root" in a rootless container is not host root: it still has every capability dropped,
    ///         no-new-privileges set and a read-only root filesystem, and it maps to an unprivileged host account —
    ///         strictly less privileged than the engine process that created it. An explicit operator-configured id
    ///         wins over both defaults, because a daemon may map identities in a way neither rule describes.
    ///     </para>
    /// </summary>
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

        return new ResolvedContainerIdentity(userId, groupId);
    }

    private static ResolvedContainerIdentity ResolveIdentity(ContainerSandboxOptions options, bool daemonIsRootless)
    {
        return ResolveIdentity(options,
            daemonIsRootless,
            static () => OperatingSystem.IsWindows() ? WindowsDefaultUserId : (int)GetEffectiveUserId(),
            static () => OperatingSystem.IsWindows() ? WindowsDefaultUserId : (int)GetEffectiveGroupId());
    }

    /// <summary>
    ///     Decides whether the workspace mount really behaves as both sides need, from the evidence of one probe file
    ///     the container created. Returns <see langword="null" /> when the mapping is sound, or the reason it is not.
    ///     <para>
    ///         A pure function because it is the half that has to be tested against mappings this machine cannot
    ///         produce. It exists at all because <c>inspect</c> cannot answer the question: the daemon echoes back the
    ///         UID it was <em>asked</em> for and has nothing to say about what that UID maps to, so a read-back that
    ///         agrees perfectly is compatible with a container that cannot write a byte. One probe settles three
    ///         things at once — the mount is writable from inside, it is backed by the host directory the engine
    ///         thinks it is, and what the container creates belongs to the engine — under either daemon mode and
    ///         without trusting the <c>rootless</c> label.
    ///     </para>
    /// </summary>
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
            throw new SandboxCapabilityNotSupportedException("The docker sandbox provider has no approved container image configured. Set "
                                                             + $"'{ContainerSandboxOptions.SectionName}:Image' to a digest-pinned reference.");
        }

        if (request.TrustedHostWorkspace is null)
        {
            // This provider creates exactly one engine-generated mount, and it is the workspace. Without it the container
            // would have nothing to act on, and a container with no workspace is not a sandbox, it is an idle process.
            throw new SandboxCapabilityNotSupportedException("The docker sandbox provider requires an engine-managed trusted host workspace on the create request.");
        }

        // A container does have a filesystem boundary, but it is NOT the one SandboxIsolationMode.Filesystem describes:
        // that mode's contract is the bubblewrap chain's — a named read-only tree list, an invented /etc, one writable
        // jail — and none of it is implemented here. Serving the request on the strength of "a container is also
        // isolated" would hand the caller a different boundary than the one it asked for, so it is refused until this
        // provider implements the same contract.
        if (request.Isolation == SandboxIsolationMode.Filesystem)
        {
            throw new SandboxCapabilityNotSupportedException("The docker sandbox provider does not implement SandboxIsolationMode.Filesystem; its container boundary is a different contract "
                                                             + "(no ReadOnlyTrees, no synthetic /etc, no jail-backed /tmp). Gate the request on SupportsFilesystemIsolation.");
        }

        // `None` and `Unrestricted` both have a mechanism and are both served exactly as asked; `Restricted` does not
        // and is rejected here rather than downgraded, because an allow-list quietly served as an open bridge is the
        // silent weakening this contract exists to prevent. Rejecting before creating also keeps the caller from ever
        // having to reason about a container that should not have existed.
        _ = DockerSandboxHardening.ResolveNetworkMode(request.NetworkPolicy);

        ValidateMountTargets(request, options, Path.GetFullPath(request.TrustedHostWorkspace.RootPath));
    }

    /// <summary>
    ///     Rejects, before anything is created, every engine-generated mount this provider could not place exactly as
    ///     asked.
    ///     <para>
    ///         The overlap sweep is <em>N-way</em> and shared with startup validation
    ///         (<see cref="ContainerSandboxOptionsValidator.FindOverlap" />). Two configured targets need one
    ///         comparison; the workspace, both tmpfs mounts and an open-ended list of runtime mounts need every pair,
    ///         and a mount placed at an ancestor of another silently hides everything the descendant was meant to
    ///         expose — after which the daemon's read-back still agrees, because the daemon was asked for exactly that.
    ///     </para>
    ///     <para>
    ///         One nesting is legitimate and is the reason this is not a flat "no overlaps" rule: a <em>file</em> mount
    ///         layered over a directory mount, which is how <c>&lt;workspace&gt;/.git/config</c> is made read-only
    ///         without making the work tree read-only. A file mount replaces exactly one path and can hide nothing
    ///         else, so it is admitted while a directory nested inside another directory is not.
    ///     </para>
    /// </summary>
    private static void ValidateMountTargets(SandboxCreateRequest request, ContainerSandboxOptions options, string workspaceRoot)
    {
        var strict = new List<ContainerMountTarget>
        {
            new(nameof(ContainerSandboxOptions.WorkspaceMountTarget), options.WorkspaceMountTarget),
            new(nameof(ContainerSandboxOptions.ScratchMountTarget), options.ScratchMountTarget),
            new(nameof(ContainerSandboxOptions.TempMountTarget), options.TempMountTarget)
        };
        var overlays = new List<string>();

        foreach (var mount in request.Mounts ?? [])
        {
            ValidateMountTarget(mount);
            var isFile = File.Exists(mount.HostPath);
            if (!isFile && !Directory.Exists(mount.HostPath))
            {
                // A bind source the daemon has never seen is created BY the daemon, owned by whatever the daemon runs
                // as — which under a rootful daemon is root, and the container then cannot write its own HOME. Refused
                // here so the failure names the missing directory instead of surfacing as a permission error inside a
                // build.
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
                strict.Add(new ContainerMountTarget("mount " + target, target));
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
            var daemon = await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var identity = ResolveIdentity(options, daemon.IsRootless);

            var bindMounts = BuildBindMounts(request, options, workspaceRoot);
            var specification = DockerSandboxHardening.BuildSpecification(options,
                identity,
                "xe-dev-" + sandboxId,
                sandboxId,
                _installId,
                bindMounts,
                // Honored, not ignored. This provider advertises SupportsResourceLimits, so a caller's ceiling must be
                // the ceiling that gets applied — and because it is baked into the specification, the read-back
                // verification below checks the caller's numbers rather than the engine's defaults.
                request.ResourceLimits,
                request.NetworkPolicy);

            containerId = await client.CreateContainerAsync(specification, cancellationToken).ConfigureAwait(false);
            await client.StartContainerAsync(containerId, cancellationToken).ConfigureAwait(false);

            var observed = await client.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
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

            await VerifyWorkspaceMappingAsync(client, containerId, workspaceRoot, options.WorkspaceMountTarget, identity, cancellationToken)
                .ConfigureAwait(false);

            var handle = new SandboxHandle
            {
                ProviderName = Name,
                SandboxId = sandboxId,
                AttachKey = request.AttachKey,
                CreatedAt = _timeProvider.GetUtcNow(),
                ManifestVersion = request.AttachKey.ManifestVersion,
                // Read off the SPECIFICATION, which is the same list the read-back was verified against — so what the
                // handle reports is what the daemon confirmed it applied, not what the caller asked for.
                Mounts = [.. bindMounts.Select(static mount => new SandboxMountBinding(mount.HostPath, mount.ContainerPath, mount.ReadOnly))],
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
                await SafeRemoveAsync(client, containerId).ConfigureAwait(false);
            }

            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Turns the neutral mount contract into Docker bind mounts: the workspace first, then every engine-generated
    ///     mount at the target it asked for.
    ///     <para>
    ///         The list this returns is the SAME list handed to <see cref="DockerSandboxHardening.BuildSpecification" />
    ///         and therefore the same one <c>DockerSandboxHardening.VerifyMounts</c> checks the daemon's read-back
    ///         against — both that every requested mount is present with the propagation and read-only flag it asked
    ///         for, and that the container carries no mount the engine did NOT request.
    ///         Composing a second list here would route these mounts around that check while leaving it looking intact.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     Where one engine-generated mount lands inside the container.
    ///     <para>
    ///         A host path <em>inside</em> the trusted workspace is DERIVED from the workspace mount target and its own
    ///         relative path, and the requested <see cref="SandboxMount.SandboxPath" /> is not consulted. That is not a
    ///         convenience: the engine must be able to ask for a nested mount — the read-only <c>.git/config</c> is the
    ///         one that matters — without knowing what the workspace is called inside a container, which is exactly the
    ///         Docker-shaped knowledge the neutral contract forbids it from having. Deriving it also makes the nesting
    ///         correct by construction rather than by two sides agreeing on a string.
    ///     </para>
    ///     <para>
    ///         Everything else is placed at the path the caller asked for, validated absolute and non-overlapping
    ///         beforehand. A per-task HOME or package cache must NOT land inside the repository work tree, so those live
    ///         outside the workspace mount and their requested target is the only thing that can say where.
    ///     </para>
    /// </summary>
    private static string ResolveMountTarget(SandboxMount mount, ContainerSandboxOptions options, string workspaceRoot)
    {
        // The one shape derivation cannot express: a mount whose SOURCE is engine-generated content outside the
        // workspace but whose TARGET is a path inside it — shadowing a committed credential without touching the real
        // file. ResolveContainerPath still rejects every '..' escape, so a caller cannot name a target outside the
        // workspace mount this way.
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
    ///     then reading it back from the host. Throws <see cref="SandboxCapabilityNotSupportedException" /> — leaving
    ///     the caller's <c>catch</c> to remove the container — when it is not.
    ///     <para>
    ///         This is the half of the hardening contract's user check that the daemon cannot perform for us. An inspect only echoes
    ///         back the UID that was asked for; it has no way to say what that UID maps to, and under a rootless daemon
    ///         a perfectly conformant read-back is compatible with a container that cannot write a single byte into its
    ///         own workspace. One probe settles the mount's writability, the identity mapping and the engine's own
    ///         access to what the container creates, under either daemon mode and without trusting the
    ///         <c>rootless</c> label the daemon reports about itself.
    ///     </para>
    /// </summary>
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
                                          cancellationToken)
                                      .ConfigureAwait(false);

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
            await client.RemoveContainerAsync(containerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DockerRuntimeException exception)
        {
            // A container that could not be removed is a leak, not a reason to swallow the original rejection. Logged
            // loudly; the startup reaper is the thing that finally collects it, and it is later work.
            _logger.LogError(exception, "Failed to remove container {ContainerId} after a fail-closed create.", containerId);
        }
    }

    /// <summary>
    ///     A container's mounts are fixed at creation, so an attach that asks for a different set cannot be served. It
    ///     is refused rather than ignored: silently returning the old container would hand the caller a handle whose
    ///     reported mapping is right and whose CONTENT is a set of mounts it did not ask for.
    /// </summary>
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
    ///     Removes every container this INSTALLATION created that no live sandbox references. Best-effort and
    ///     idempotent: a removal that fails is logged and the sweep continues, and a second run over an already-swept
    ///     daemon finds nothing.
    ///     <para>
    ///         The leak it collects is the one the in-memory registry cannot: <see cref="DisposeAsync" /> and
    ///         <see cref="KillAsync" /> remove containers on a graceful shutdown or an explicit kill, but a hard host
    ///         kill runs neither, and the container is then referenced only by a dictionary that died with the
    ///         process. Nothing else reaps it — <c>SandboxOrphanReaper</c> reads on-disk markers written by the
    ///         process provider and knows nothing about containers. It also unblocks the next create: a leaked
    ///         container still owns its <c>xe-dev-&lt;sandboxId&gt;</c> name, and the same attach key would collide
    ///         with it forever.
    ///     </para>
    ///     <para>
    ///         <b>What it will not touch.</b> The daemon-side filter is <see cref="DockerSandboxHardening.OwnerLabel" />
    ///         AND <see cref="DockerSandboxHardening.InstallLabel" />, so a container belonging to another XE
    ///         installation on the same daemon — or to anything else at all — is never a candidate, and a container
    ///         from a build that predates the install label is left alone rather than guessed about. The engine
    ///         creates no networks and no volumes (every container is created on <c>none</c> or the default bridge,
    ///         and its removal already takes anonymous volumes with it), so there is nothing else of ours to sweep.
    ///     </para>
    /// </summary>
    /// <returns>How many containers were removed.</returns>
    internal async Task<int> SweepOrphanedContainersAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = DockerDaemonEndpointResolver.Resolve(_options.CurrentValue);
        var client = _clientFactory.Create(endpoint);

        try
        {
            var live = _sandboxes.Values.Select(static state => state.ContainerId).ToHashSet(StringComparer.Ordinal);
            var owned = await client.ListContainersAsync(BuildOwnershipFilter(), cancellationToken).ConfigureAwait(false);
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
                    await client.RemoveContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
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
            await client.DisposeAsync().ConfigureAwait(false);
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
    ///     <para>
    ///         Derived from the node data directory, because that is the only identity available at startup that is
    ///         both stable across restarts and different per installation: the daemon attestation is per node but
    ///         lives inside this directory, and the sandbox id is a hash over an attach key that does not exist yet.
    ///         Hashed rather than used raw for the same reason <see cref="BuildSandboxId" /> hashes: the path
    ///         routinely contains the operator's account name, and a container label is readable by anyone who can
    ///         list containers on that daemon.
    ///     </para>
    ///     <para>
    ///         Two installations sharing one node data directory would share an id and sweep each other's containers.
    ///         That configuration is already excluded — the node database, the attestation and the process provider's
    ///         jail root all assume a single owner of this directory — so it is not defended against here beyond
    ///         being written down.
    ///     </para>
    /// </summary>
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
