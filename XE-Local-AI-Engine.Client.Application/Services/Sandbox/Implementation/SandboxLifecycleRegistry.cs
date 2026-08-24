namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     Owns the live process-jail set of one <see cref="ProcessSandboxRuntimeProvider" />: create-or-attach by key,
///     reconnect, lookup, owner-conflict eviction and termination. The provider keeps command execution and process
///     control; everything that decides which jails exist lives here.
///     <para>
///         There is exactly ONE dictionary and ONE lock, and they are here — the provider holds no copy and takes no
///         second lock. Every provider operation reaches a <see cref="JailState" /> through
///         <see cref="GetAliveState" />, <see cref="FindState" />, <see cref="RemoveAndTerminate" /> or
///         <see cref="TerminateAll" />, so the create/attach/evict/kill decisions stay serialized against each other
///         exactly as they were when they shared the provider's class body.
///     </para>
///     <para>
///         Attach is idempotent by <see cref="SandboxAttachKey" />: a create request that matches a live jail returns
///         that jail's handle instead of a second one, after checking the trusted-host-workspace binding still agrees
///         and letting a stricter jail-disk ceiling tighten the live one (<see cref="JailState.TightenMaxJailDiskBytes" />).
///         A same-node request under a DIFFERENT owner is not an attach — the old jail is killed and removed before
///         the new one is created, so one user's sandbox is never handed to another.
///     </para>
/// </summary>
internal sealed class SandboxLifecycleRegistry
{
    private readonly string _jailRoot;
    private readonly ISandboxLauncher _launcher;
    private readonly ConcurrentDictionary<string, JailState> _sandboxes = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;

    public SandboxLifecycleRegistry(string jailRoot, ISandboxLauncher launcher, TimeProvider timeProvider)
    {
        _jailRoot = jailRoot ?? throw new ArgumentNullException(nameof(jailRoot));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail-closed capability contract, resolved against what this HOST can actually do. Anything the provider
        // cannot deliver here is refused up front rather than silently downgraded; anything it can is captured in the
        // launch policy and applied to every command this sandbox runs.
        var launchPolicy = BuildLaunchPolicy(request);

        lock (_sync)
        {
            var attached = FindAliveByKey(request.AttachKey);
            if (attached is not null)
            {
                EnsureCompatibleWorkspaceBinding(attached, request.TrustedHostWorkspace);

                // The disk ceiling is a CREATE-TIME property of the sandbox, so an attach cannot re-specify it — but it
                // can ask for LESS, and refusing that would be the wrong asymmetry: the request that wants a tighter
                // bound is the one being careful. It takes effect for future commands only (see the method's docs).
                attached.TightenMaxJailDiskBytes(request.MaxJailDiskBytes);
                return Task.FromResult(attached.Handle);
            }

            // Owner change on the same node forbids reuse: kill and remove any sandbox keyed to the same node under a
            // different owner before creating the new one (parity with FakeSandboxRuntimeProvider.EvictOwnerConflicts).
            EvictOwnerConflicts(request.AttachKey);

            var sandboxId = BuildSandboxId(request.AttachKey);
            var jailDirectory = request.TrustedHostWorkspace is null
                ? Path.Combine(_jailRoot, sandboxId)
                : ResolveTrustedHostWorkspace(request.TrustedHostWorkspace.RootPath);
            Directory.CreateDirectory(jailDirectory);

            var handle = new SandboxHandle
            {
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                SandboxId = sandboxId,
                AttachKey = request.AttachKey,
                CreatedAt = _timeProvider.GetUtcNow(),
                ManifestVersion = request.AttachKey.ManifestVersion,
                Mounts = ResolveIdentityMounts(request, jailDirectory),
                // A host child sees host paths, so the jail directory names the same bytes inside and out. That
                // identity is what lets a caller compose a child-visible path UNDER the jail — which is where anything
                // the jail disk watchdog is supposed to meter has to live.
                WorkingRoot = jailDirectory
            };
            _sandboxes[sandboxId] = new JailState(handle,
                jailDirectory,
                launchPolicy,
                request.TrustedHostWorkspace is not null,
                request.MaxJailDiskBytes);
            return Task.FromResult(handle);
        }
    }

    public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var match = FindAliveByKey(attachKey)
                        ?? throw new SandboxHandleInvalidException("No live sandbox matches the supplied attach key.");
            return Task.FromResult(match.Handle);
        }
    }

    public JailState GetAliveState(SandboxHandle handle)
    {
        if (_sandboxes.TryGetValue(handle.SandboxId, out var state) && state.Alive)
        {
            return state;
        }

        throw new SandboxHandleInvalidException($"Sandbox '{handle.SandboxId}' is no longer available.");
    }

    /// <summary>
    ///     The dead-tolerant lookup used by best-effort command cancellation: a missing id is a no-op there, so this
    ///     returns <see langword="null" /> rather than throwing the way <see cref="GetAliveState" /> does.
    /// </summary>
    public JailState? FindState(string sandboxId)
    {
        return _sandboxes.TryGetValue(sandboxId, out var state) ? state : null;
    }

    /// <summary>Removes one jail and tears it down, under the same lock that creates and evicts jails.</summary>
    public void RemoveAndTerminate(string sandboxId)
    {
        lock (_sync)
        {
            if (_sandboxes.TryRemove(sandboxId, out var state))
            {
                TerminateState(state, SandboxScopeUnitKiller.TryCreate(_launcher.Containment.FilesystemIsolation));
            }
        }
    }

    /// <summary>
    ///     Tears down every remaining jail. Best-effort by construction: a sandbox can already be gone, and this runs
    ///     on the provider's dispose path where throwing would strand the rest.
    /// </summary>
    public void TerminateAll()
    {
        foreach (var state in _sandboxes.Values)
        {
            TerminateState(state, SandboxScopeUnitKiller.TryCreate(_launcher.Containment.FilesystemIsolation));
        }

        _sandboxes.Clear();
    }

    /// <summary>
    ///     The enforcement half of the capability-honesty invariant: a guarantee this host cannot deliver is refused up
    ///     front, so a caller can never believe it received isolation the provider did not apply. It reads the same
    ///     containment probe as <see cref="ProcessSandboxRuntimeProvider.Capabilities" />, so what is rejected here is
    ///     exactly what is not advertised there.
    /// </summary>
    private SandboxLaunchPolicy BuildLaunchPolicy(SandboxCreateRequest request)
    {
        var containment = _launcher.Containment;

        // Restricted means an egress allow-list, which needs a veth pair plus a per-namespace ruleset. That is
        // explicitly out of scope (default-deny only), so it is never honored regardless of host capability.
        if (request.NetworkPolicy == SandboxNetworkPolicy.Restricted)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider has no network allow-list mechanism and cannot honor NetworkPolicy.Restricted. Use NetworkPolicy.None for default-deny egress, or an OS-isolated provider for an allow-list."));
        }

        // A filesystem boundary is the one request that is never a preference: a caller asking for it is asking to be
        // TOLD when it is not there, because everything it does next depends on the answer. Rejected fail-closed on a
        // host the probe could not measure it on, with the measured reason attached.
        var wantsIsolation = request.Isolation == SandboxIsolationMode.Filesystem;
        if (wantsIsolation && !containment.SupportsFilesystemIsolation)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider cannot isolate the host filesystem on this host ({containment.FilesystemIsolationUnavailableReason ?? "no mechanism is available"}), so SandboxIsolationMode.Filesystem cannot be honored. Gate the request on SupportsFilesystemIsolation, or use a provider that advertises it."));
        }

        // An isolated jail is tightened to 0700 and is never exposed at its host pathname, neither of which is a
        // thing to do to a user's own checkout. The two features are therefore refused together rather than one
        // silently reshaping the other's directory.
        if (wantsIsolation && request.TrustedHostWorkspace is not null)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider cannot combine SandboxIsolationMode.Filesystem with a trusted host workspace: the isolated jail is private to one sandbox and is not reachable at its host path, which is the opposite of what a preserved workspace is for."));
        }

        // Read-only trees only mean something inside a mount namespace. Under the non-isolated mode the sandbox can
        // already read the whole host filesystem, so accepting the list would advertise a narrowing that did not
        // happen — the same silent downgrade the branches around it exist to prevent.
        if (!wantsIsolation && request.ReadOnlyTrees is { Count: > 0 })
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider only binds read-only trees under SandboxIsolationMode.Filesystem; without it the sandbox already reads the whole host filesystem and the list would mean nothing."));
        }

        // Rejected at CREATE time as well as at launch time: a caller that named an unbindable tree has made a
        // configuration mistake, and finding out at the first command — after provisioning, in a result string — is
        // far worse than finding out when the sandbox is asked for.
        var shadowed = (request.ReadOnlyTrees ?? [])
            .FirstOrDefault(tree => !SandboxIsolatedChain.CanBindReadOnlyTree(Path.TrimEndingDirectorySeparator(Path.GetFullPath(tree))));
        if (shadowed is not null)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The read-only tree '{shadowed}' lies under a mount point the isolated sandbox owns ({string.Join(", ", SandboxIsolatedChain.ReservedMountPoints)}); it would be shadowed by the sandbox's own mounts rather than visible inside it."));
        }

        // None means no egress. Honored when the host can create an empty network namespace; rejected fail-closed when
        // it cannot, rather than handing back a sandbox that silently shares the host network. Under the isolated mode
        // the denial comes from bwrap's own --unshare-net — which the probe positively controlled with a loopback
        // connect — so the separate unshare(1) mechanism is not additionally required.
        var denyEgress = request.NetworkPolicy == SandboxNetworkPolicy.None;
        if (denyEgress && !wantsIsolation && !containment.SupportsNetworkIsolation)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider cannot deny network egress on this host ({containment.NetworkIsolationUnavailableReason ?? "no mechanism is available"}), so NetworkPolicy.None cannot be honored. Use NetworkPolicy.Unrestricted to accept a shared host network, or an OS-isolated provider."));
        }

        // Resource limits are honored when a transient systemd user scope can impose them, and rejected fail-closed
        // when it cannot — running without the ceiling the caller asked for is exactly the silent downgrade this
        // contract exists to prevent.
        var limits = request.ResourceLimits;
        var wantsLimits = limits is not null && (limits.CpuCount.HasValue || limits.MemoryMb.HasValue || limits.PidsLimit.HasValue);
        if (wantsLimits && !wantsIsolation && !containment.SupportsResourceLimits)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider cannot enforce resource limits (CPU/memory/PID) on this host ({containment.ResourceLimitsUnavailableReason ?? "no mechanism is available"}). Remove SandboxResourceLimits or use a provider that advertises SupportsResourceLimits."));
        }

        // A read-only mount needs a mount layer, and this provider has none — the child runs on the host filesystem
        // with ordinary permissions. Rejected rather than served writable: a caller that asked for read-only and got
        // read-write would believe a file was protected that anything in the sandbox can overwrite, which is the same
        // silent downgrade the network and resource-limit branches above exist to prevent. Callers that want this
        // where it exists must gate the request on SupportsReadOnlyMounts, exactly as AgentHome gates its egress
        // request on SupportsNetworkPolicy.
        if (request.Mounts?.Any(static mount => mount.ReadOnly) == true)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider has no mount layer and cannot make a mount read-only. Remove the read-only mount, or use a provider that advertises SupportsReadOnlyMounts."));
        }

        return new SandboxLaunchPolicy
        {
            ResourceLimits = wantsLimits ? limits : null,
            DenyNetworkEgress = denyEgress,
            Isolation = wantsIsolation ? SandboxIsolationMode.Filesystem : SandboxIsolationMode.None,
            ReadOnlyTrees = wantsIsolation ? [.. request.ReadOnlyTrees ?? []] : [],
            ThreadLimit = request.ThreadLimit ?? 1,
            Role = request.RuntimeProfile
        };
    }

    /// <summary>
    ///     Resolves the engine's requested mounts as an IDENTITY map: a host child already sees the host filesystem, so
    ///     every requested host path is reachable under its own name and nothing is mounted anywhere.
    ///     <para>
    ///         The requested <see cref="SandboxMount.SandboxPath" /> is therefore <em>discarded</em>, not honoured, and
    ///         the handle reports the host path instead. That is the honest answer rather than a shortcut: a caller that
    ///         put the requested path into a child's environment would name a directory this provider never created.
    ///     </para>
    ///     <para>
    ///         This deliberately does NOT start confining anything. The mount list is a description of what the sandbox
    ///         can reach, and under this provider that set was already the whole host filesystem; narrowing it here
    ///         would change the preserved-workspace contract under callers that never asked for it.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<SandboxMountBinding> ResolveIdentityMounts(SandboxCreateRequest request, string jailDirectory)
    {
        var bindings = new List<SandboxMountBinding>();
        if (request.TrustedHostWorkspace is not null)
        {
            bindings.Add(new SandboxMountBinding(jailDirectory, jailDirectory, ReadOnly: false));
        }

        foreach (var mount in request.Mounts ?? [])
        {
            if (string.IsNullOrWhiteSpace(mount.HostPath))
            {
                throw new ArgumentException("An engine-generated sandbox mount must name a host path.", nameof(request));
            }

            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount.HostPath));
            if (!Directory.Exists(canonical) && !File.Exists(canonical))
            {
                throw new DirectoryNotFoundException($"The engine-generated sandbox mount source '{mount.HostPath}' does not exist.");
            }

            bindings.Add(new SandboxMountBinding(canonical, canonical, mount.ReadOnly));
        }

        return bindings;
    }

    private JailState? FindAliveByKey(SandboxAttachKey attachKey)
    {
        return _sandboxes.Values.FirstOrDefault(state => state.Alive && state.Handle.AttachKey == attachKey);
    }

    private static void EnsureCompatibleWorkspaceBinding(JailState state, SandboxTrustedHostWorkspace? requested)
    {
        if (requested is null)
        {
            if (state.PreserveJailRoot)
            {
                throw new SandboxHandleInvalidException("The existing sandbox is bound to a trusted host workspace, but the attach request is not.");
            }

            return;
        }

        var requestedRoot = ResolveTrustedHostWorkspace(requested.RootPath);
        if (!state.PreserveJailRoot || !string.Equals(requestedRoot, state.JailRoot, StringComparison.Ordinal))
        {
            throw new SandboxHandleInvalidException("The existing sandbox is bound to a different trusted host workspace.");
        }
    }

    private static string ResolveTrustedHostWorkspace(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var canonical = Path.GetFullPath(rootPath);
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException("The trusted host workspace must be an existing canonical directory.");
        }

        SandboxJailPathGuard.EnsureNoSymbolicLinkComponents(canonical);

        return canonical;
    }

    private void EvictOwnerConflicts(SandboxAttachKey attachKey)
    {
        var conflicts = _sandboxes
                        .Where(entry => string.Equals(entry.Value.Handle.AttachKey.NodeId, attachKey.NodeId, StringComparison.Ordinal)
                                        && !string.Equals(entry.Value.Handle.AttachKey.OwnerUserId, attachKey.OwnerUserId, StringComparison.Ordinal))
                        .Select(entry => entry.Key)
                        .ToList();

        foreach (var sandboxId in conflicts)
        {
            if (_sandboxes.TryRemove(sandboxId, out var state))
            {
                TerminateState(state, SandboxScopeUnitKiller.TryCreate(_launcher.Containment.FilesystemIsolation));
            }
        }
    }

    private static string BuildSandboxId(SandboxAttachKey attachKey)
    {
        // Hash the complete attach scope. Owner/node alone is insufficient because AgentHome and Development may use
        // different runtime profiles or manifest versions for the same logical node and must coexist without one
        // dictionary entry overwriting the other.
        var scope = string.Concat(attachKey.OwnerUserId, "\0",
            attachKey.NodeId, "\0",
            attachKey.ProviderName, "\0",
            attachKey.RuntimeProfile, "\0",
            attachKey.ManifestVersion.ToString(CultureInfo.InvariantCulture));
        var scopeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..16];
        var nodeSegment = SanitizeSegment(attachKey.NodeId);
        return string.Create(CultureInfo.InvariantCulture, $"process-{nodeSegment}-{scopeHash}");
    }

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        return builder.Length == 0 ? "node" : builder.ToString();
    }

    private static void TerminateState(JailState state, SandboxScopeUnitKiller? scopeKiller)
    {
        lock (state.Sync)
        {
            if (!state.Alive)
            {
                return;
            }

            state.MarkDead();
        }

        foreach (var inFlight in state.InFlight.Values)
        {
            // Signal the in-flight ExecuteAsync that its command was cancelled (Completed=false) AND tree-kill the
            // process so a sandbox kill terminates every running command immediately.
            inFlight.RequestCancel();
            // For an isolated command the tree-kill alone is not enough: its processes live in a PID namespace this
            // process cannot see, so the scope's cgroup is signalled FIRST and the tree-kill is what finishes the
            // outer helpers. Synchronous on purpose — the jail directory is deleted a few lines below, and deleting a
            // directory processes are still writing to is how a teardown leaves a half-removed jail behind.
            if (inFlight.ScopeUnitName is { } unitName)
            {
                scopeKiller?.Kill(unitName);
            }

            SandboxProcessTree.TreeKill(inFlight.Process);
        }

        state.InFlight.Clear();

        try
        {
            if (!state.PreserveJailRoot && Directory.Exists(state.JailRoot))
            {
                Directory.Delete(state.JailRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort jail teardown; a locked file does not fail the kill.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort jail teardown.
        }
    }
}
