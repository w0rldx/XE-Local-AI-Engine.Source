namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Owns the live process-jail set of one <see cref="ProcessSandboxRuntimeProvider" />: create-or-attach by key, reconnect, lookup,
///     owner-conflict eviction and termination. The provider keeps command execution and process control.
/// </summary>
/// <remarks>
///     There is exactly ONE dictionary and ONE lock, both here: the provider holds no copy and every provider operation reaches a
///     <see cref="JailState" /> through this type, so create, attach, evict and kill stay serialized against each other. Attach is
///     idempotent by <see cref="SandboxAttachKey" /> — a create matching a live jail returns that jail's handle once the
///     trusted-host-workspace binding still agrees and a stricter disk ceiling has tightened the live one. A same-node request under a
///     DIFFERENT owner is not an attach: the old jail is killed and removed first, so one user's sandbox never reaches another.
/// </remarks>
internal sealed class SandboxLifecycleRegistry
{
    /// <summary>How long one teardown call (one jail, an eviction or terminate-all) waits in total for killed MXC children to exit.</summary>
    internal static readonly TimeSpan InFlightExitBudget = TimeSpan.FromSeconds(2);

    private readonly string _jailRoot;
    private readonly ISandboxLauncher _launcher;
    private readonly ILogger _logger;
    private readonly IExecutionPreviewPolicy? _previewPolicy;
    private readonly ConcurrentDictionary<string, JailState> _sandboxes = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;

    public SandboxLifecycleRegistry(string jailRoot,
        ISandboxLauncher launcher,
        TimeProvider timeProvider,
        IExecutionPreviewPolicy? previewPolicy = null,
        ILogger? logger = null)
    {
        _jailRoot = jailRoot ?? throw new ArgumentNullException(nameof(jailRoot));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _previewPolicy = previewPolicy;
        _logger = logger ?? NullLogger.Instance;
    }

    public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail-closed capability contract, resolved against what this HOST can actually do: anything the provider cannot deliver is
        // refused up front rather than silently downgraded, and anything it can is captured in the launch policy for every command.
        var launchPolicy = BuildLaunchPolicy(request);

        lock (_sync)
        {
            var attached = FindAliveByKey(request.AttachKey);
            if (attached is not null)
            {
                EnsureCompatibleWorkspaceBinding(attached, request.TrustedHostWorkspace);

                // The disk ceiling is a CREATE-TIME property, so an attach cannot re-specify it, but it can ask for LESS: refusing that
                // would be the wrong asymmetry, the tighter request being the careful one. Future commands only (see the method's docs).
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
            var chained = IsChained(launchPolicy);
            if (chained)
            {
                // Resolved before anything is created, so a mount the chain cannot place refuses the create and leaves nothing behind.
                launchPolicy = launchPolicy with
                {
                    Binds = ResolveIsolatedBinds(request, jailDirectory),
                    PrivateRoot = request.TrustedHostWorkspace is null ? null : Path.Combine(_jailRoot, sandboxId)
                };
            }

            Directory.CreateDirectory(jailDirectory);

            var handle = new SandboxHandle
            {
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                SandboxId = sandboxId,
                AttachKey = request.AttachKey,
                CreatedAt = _timeProvider.GetUtcNow(),
                ManifestVersion = request.AttachKey.ManifestVersion,
                Mounts = chained ? ResolveIsolatedMounts(launchPolicy, request, jailDirectory) : ResolveIdentityMounts(request, jailDirectory),
                // What the launch policy RESOLVED to, after every fail-closed refusal above — so the handle reports
                // the boundary the sandbox really has rather than the one its caller asked for.
                Isolation = launchPolicy.Isolation,
                // A host child sees host paths, so the jail names the same bytes inside and out, which is what lets a caller compose a
                // child-visible path under it. Under bwrap the CHILD sees it at SandboxIsolatedPaths.Posix.Work; this stays the HOST path.
                WorkingRoot = jailDirectory,
                IsolatedPaths = ResolveIsolatedPaths(launchPolicy, jailDirectory)
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
                using var exitBudget = NewExitBudget();
                TerminateState(state, SandboxScopeUnitKiller.TryCreate(_launcher.Containment.FilesystemIsolation), exitBudget.Token);
            }
        }
    }

    /// <summary>
    ///     Tears down every remaining jail. Best-effort by construction: a sandbox can already be gone, and this runs
    ///     on the provider's dispose path where throwing would strand the rest.
    /// </summary>
    public void TerminateAll()
    {
        // ONE budget for every jail, so a shutdown blocks at most InFlightExitBudget however many sandboxes are live.
        using var exitBudget = NewExitBudget();
        foreach (var state in _sandboxes.Values)
        {
            TerminateState(state, SandboxScopeUnitKiller.TryCreate(_launcher.Containment.FilesystemIsolation), exitBudget.Token);
        }

        _sandboxes.Clear();
    }

    /// <summary>
    ///     The enforcement half of the capability-honesty invariant: a guarantee this host cannot deliver is refused up front, so a caller
    ///     can never believe it received isolation the provider did not apply.
    /// </summary>
    /// <remarks>
    ///     It reads the same containment probe as <see cref="ProcessSandboxRuntimeProvider.Capabilities" />, so what is rejected here is
    ///     exactly what is not advertised there.
    /// </remarks>
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

        // A filesystem boundary is never a preference: a caller asking for it is asking to be TOLD when it is absent, since everything it
        // does next depends on the answer. Rejected fail-closed where the probe could not measure it, with the measured reason attached.
        var wantsIsolation = request.Isolation == SandboxIsolationMode.Filesystem;

        // bwrap serves it where measured; otherwise the AppContainer boundary, only while ELIGIBLE, decided now (ADR 0019).
        var appContainerBoundary = wantsIsolation && !containment.SupportsFilesystemIsolation
            ? containment.EligibleAppContainerBoundary(_previewPolicy?.PreviewMechanismsEnabled == true)
            : null;
        if (wantsIsolation && !containment.SupportsFilesystemIsolation && appContainerBoundary is null)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider cannot isolate the host filesystem on this host ({FilesystemUnavailableReason(containment)}), so SandboxIsolationMode.Filesystem cannot be honored. Gate the request on SupportsFilesystemIsolation, or use a provider that advertises it."));
        }

        // Under bwrap a trusted workspace becomes the 0700 jail at /work, right for an engine-owned clone and never for an operator's checkout.
        // The AppContainer boundary grants by DACL on the directory itself, so the combination stays refused there.
        if (appContainerBoundary is not null && request.TrustedHostWorkspace is not null)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider cannot combine the {appContainerBoundary.Mechanism} AppContainer boundary with a trusted host workspace: the boundary grants by access-control entries on the directory, which is not a thing to do to a preserved workspace."));
        }

        // Read-only trees only mean something inside a mount namespace: the non-isolated mode already reads the whole host filesystem, so
        // accepting the list would advertise a narrowing that did not happen — the downgrade the branches around it exist to prevent.
        if (!wantsIsolation && request.ReadOnlyTrees is { Count: > 0 })
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider only binds read-only trees under SandboxIsolationMode.Filesystem; without it the sandbox already reads the whole host filesystem and the list would mean nothing."));
        }

        // Rejected at CREATE time as well as at launch time: naming an unbindable tree is a configuration mistake, and finding out at the
        // first command — after provisioning, in a result string — is far worse than finding out when the sandbox is asked for.
        var shadowed = appContainerBoundary is not null // bwrap's rule only: the AppContainer boundary owns no mount points
            ? null
            : (request.ReadOnlyTrees ?? []).FirstOrDefault(tree => !SandboxIsolatedChain.CanBindReadOnlyTree(Path.TrimEndingDirectorySeparator(Path.GetFullPath(tree))));
        if (shadowed is not null)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The read-only tree '{shadowed}' lies under a mount point the isolated sandbox owns ({string.Join(", ", SandboxIsolatedChain.ReservedMountPoints)}); it would be shadowed by the sandbox's own mounts rather than visible inside it."));
        }

        // None means no egress: honored where the host can create an empty network namespace, rejected fail-closed where it cannot
        // rather than silently sharing the host network. Under isolation the denial is bwrap's own --unshare-net, probe-controlled.
        var denyEgress = request.NetworkPolicy == SandboxNetworkPolicy.None;
        if (denyEgress && !wantsIsolation && !containment.SupportsNetworkIsolation)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider cannot deny network egress on this host ({containment.NetworkIsolationUnavailableReason ?? "no mechanism is available"}), so NetworkPolicy.None cannot be honored. Use NetworkPolicy.Unrestricted to accept a shared host network, or an OS-isolated provider."));
        }

        // Resource limits are honored where a transient systemd user scope can impose them and rejected fail-closed where it cannot:
        // running without the ceiling the caller asked for is exactly the silent downgrade this contract exists to prevent.
        var limits = request.ResourceLimits;
        var wantsLimits = limits is not null && (limits.CpuCount.HasValue || limits.MemoryMb.HasValue || limits.PidsLimit.HasValue);
        // Isolation skips the systemd gate below only for bwrap's chain; the AppContainer boundary has no ceiling at all (ADR 0019 Q3).
        if (wantsLimits && appContainerBoundary is not null)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider serves SandboxIsolationMode.Filesystem here through the {appContainerBoundary.Mechanism} AppContainer boundary (Preview, ADR 0019 Q3): the AppContainer boundary enforces no CPU, memory or process ceiling; omit SandboxResourceLimits for a timeout-only run."));
        }

        if (wantsLimits && !wantsIsolation && !containment.SupportsResourceLimits)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{ProcessSandboxRuntimeProvider.Name}' sandbox provider cannot enforce resource limits (CPU/memory/PID) on this host ({containment.ResourceLimitsUnavailableReason ?? "no mechanism is available"}). Remove SandboxResourceLimits or use a provider that advertises SupportsResourceLimits."));
        }

        // A read-only mount needs a mount layer, which only the bubblewrap chain has: rejected elsewhere rather than served writable, because
        // a caller would believe a file protected that anything can overwrite.
        if ((!wantsIsolation || appContainerBoundary is not null) && request.Mounts?.Any(static mount => mount.ReadOnly) == true)
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
            AppContainerBoundary = appContainerBoundary,
            Role = request.RuntimeProfile
        };
    }

    // The view the serving mechanism presents: the namespace's POSIX paths (HOME = the private /tmp for a trusted workspace, which gains
    // no home directory), or the jail's host paths under the AppContainer boundary. Consumers compose HOME/TMPDIR from this.
    private static SandboxIsolatedPaths? ResolveIsolatedPaths(SandboxLaunchPolicy launchPolicy, string jailDirectory)
    {
        if (launchPolicy.Isolation != SandboxIsolationMode.Filesystem)
        {
            return null;
        }

        if (launchPolicy.AppContainerBoundary is not null)
        {
            return SandboxIsolatedPaths.ForHostJail(jailDirectory);
        }

        return launchPolicy.PrivateRoot is null
            ? SandboxIsolatedPaths.Posix
            : SandboxIsolatedPaths.Posix with
            {
                Home = SandboxIsolatedPaths.PosixTemp
            };
    }

    // The bubblewrap chain, the one mechanism here with a mount layer.
    private static bool IsChained(SandboxLaunchPolicy launchPolicy) =>
        launchPolicy.Isolation == SandboxIsolationMode.Filesystem && launchPolicy.AppContainerBoundary is null;

    /// <summary>
    ///     Resolves each engine-generated mount to the target the isolated chain binds it at: <c>/work</c> plus the workspace-relative path
    ///     for a shadow or a file inside the trusted workspace, the requested absolute path otherwise.
    /// </summary>
    /// <remarks>
    ///     Unlike the identity map this CONFINES: the chain shows nothing of the host but <c>/usr</c>, the read-only trees, the jail and
    ///     these binds. A target the chain cannot place (<see cref="SandboxIsolatedChain.CanBindAfterJail" />) is refused, and a relative
    ///     segment is refused rather than normalized.
    /// </remarks>
    private static IReadOnlyList<SandboxIsolatedBind> ResolveIsolatedBinds(SandboxCreateRequest request, string jailDirectory)
    {
        var binds = new List<SandboxIsolatedBind>();
        foreach (var mount in request.Mounts ?? [])
        {
            if (string.IsNullOrWhiteSpace(mount.HostPath))
            {
                throw new ArgumentException("An engine-generated sandbox mount must name a host path.", nameof(request));
            }

            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount.HostPath));
            var isFile = File.Exists(canonical);
            if (!isFile && !Directory.Exists(canonical))
            {
                throw new DirectoryNotFoundException($"The engine-generated sandbox mount source '{mount.HostPath}' does not exist.");
            }

            string target;
            if (mount.TargetIsWorkspaceRelative)
            {
                if (request.TrustedHostWorkspace is null)
                {
                    throw new SandboxCapabilityNotSupportedException("A workspace-relative sandbox mount needs a trusted host workspace to be relative to.");
                }

                target = SandboxIsolatedChain.WorkPath + "/" + RequireCleanRelative(mount.SandboxPath.TrimStart('/'), mount.SandboxPath);
            }
            else if (request.TrustedHostWorkspace is not null && PathContainment.IsUnderRoot(canonical, jailDirectory))
            {
                target = SandboxIsolatedChain.WorkPath + "/" + Path.GetRelativePath(jailDirectory, canonical).Replace(Path.DirectorySeparatorChar, '/');
            }
            else
            {
                target = "/" + RequireCleanRelative(mount.SandboxPath.TrimStart('/'), mount.SandboxPath);
                if (!mount.SandboxPath.StartsWith('/') || !SandboxIsolatedChain.CanBindAfterJail(target))
                {
                    throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                        $"The sandbox mount target '{mount.SandboxPath}' is not an absolute path outside the mount points the isolated sandbox owns ({string.Join(", ", SandboxIsolatedChain.ReservedMountPoints)})."));
                }
            }

            binds.Add(new SandboxIsolatedBind
            {
                HostPath = canonical,
                SandboxPath = target,
                ReadOnly = mount.ReadOnly,
                IsFile = isFile
            });
        }

        return binds;
    }

    private static string RequireCleanRelative(string relative, string requested)
    {
        var segments = relative.Split('/');
        if (relative.Length == 0 || segments.Any(static segment => segment is "" or "." or ".."))
        {
            throw new SandboxCapabilityNotSupportedException($"The sandbox mount target '{requested}' is not a clean path.");
        }

        return relative;
    }

    // What the chain will show: the trusted workspace at /work and every bind at its resolved target, so a caller composes child-visible
    // paths from the handle exactly as it does for a container.
    private static IReadOnlyList<SandboxMountBinding> ResolveIsolatedMounts(SandboxLaunchPolicy launchPolicy, SandboxCreateRequest request, string jailDirectory)
    {
        var bindings = new List<SandboxMountBinding>();
        if (request.TrustedHostWorkspace is not null)
        {
            bindings.Add(new SandboxMountBinding
            {
                HostPath = jailDirectory,
                SandboxPath = SandboxIsolatedChain.WorkPath,
                ReadOnly = false
            });
        }

        bindings.AddRange(launchPolicy.Binds.Select(static bind => new SandboxMountBinding
        {
            HostPath = bind.HostPath,
            SandboxPath = bind.SandboxPath,
            ReadOnly = bind.ReadOnly
        }));
        return bindings;
    }

    // A measured-but-ineligible boundary is named as such, so the refusal says what the operator can change rather than "no mechanism".
    internal static string FilesystemUnavailableReason(SandboxContainment containment)
    {
        return containment.SupportsAppContainerBoundary
            ? SandboxContainment.ExecutionPreviewsDisabledReason
            : containment.FilesystemIsolationUnavailableReason ?? "no mechanism is available";
    }

    /// <summary>
    ///     Resolves the engine's requested mounts as an IDENTITY map: a host child already sees the host filesystem, so every requested
    ///     host path is reachable under its own name and nothing is mounted anywhere.
    /// </summary>
    /// <remarks>
    ///     The requested <see cref="SandboxMount.SandboxPath" /> is DISCARDED rather than honored and the handle reports the host path,
    ///     which is the honest answer: a caller putting the requested path into a child's environment would name a directory this provider
    ///     never created. It deliberately confines nothing — the mount list describes what the sandbox can reach, already the whole host
    ///     filesystem here, and narrowing it would change the preserved-workspace contract under callers that never asked.
    /// </remarks>
    private static IReadOnlyList<SandboxMountBinding> ResolveIdentityMounts(SandboxCreateRequest request, string jailDirectory)
    {
        var bindings = new List<SandboxMountBinding>();
        if (request.TrustedHostWorkspace is not null)
        {
            bindings.Add(new SandboxMountBinding
            {
                HostPath = jailDirectory,
                SandboxPath = jailDirectory,
                ReadOnly = false
            });
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

            bindings.Add(new SandboxMountBinding
            {
                HostPath = canonical,
                SandboxPath = canonical,
                ReadOnly = mount.ReadOnly
            });
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

        if (conflicts.Count == 0)
        {
            return;
        }

        using var exitBudget = NewExitBudget();
        foreach (var sandboxId in conflicts)
        {
            if (_sandboxes.TryRemove(sandboxId, out var state))
            {
                TerminateState(state, SandboxScopeUnitKiller.TryCreate(_launcher.Containment.FilesystemIsolation), exitBudget.Token);
            }
        }
    }

    private static string BuildSandboxId(SandboxAttachKey attachKey)
    {
        // Hash the complete attach scope: owner and node alone are insufficient, because AgentHome and Development may use different
        // runtime profiles or manifest versions for one logical node and must coexist without overwriting each other's entry.
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

    private CancellationTokenSource NewExitBudget() =>
        new(InFlightExitBudget, _timeProvider);

    private void TerminateState(JailState state, SandboxScopeUnitKiller? scopeKiller, CancellationToken exitBudget)
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
            // For an isolated command a tree-kill alone is not enough — its processes live in a PID namespace this one cannot see — so the
            // scope's cgroup is signaled FIRST. Synchronous: the jail is deleted below, and deleting it mid-write half-removes it.
            if (inFlight.ScopeUnitName is { } unitName)
            {
#pragma warning disable MA0045 // forced sync: the jail directory is deleted below, see the comment above
                scopeKiller?.Kill(unitName);
#pragma warning restore MA0045
            }

            inFlight.Process.Kill();
        }

        var killedMxcChildren = state.InFlight.Values.Select(static inFlight => inFlight.Process).OfType<MxcSandboxChildProcess>().ToList();
        state.InFlight.Clear();
        WaitForExits(killedMxcChildren, exitBudget);

        try
        {
            if (!state.PreserveJailRoot && Directory.Exists(state.JailRoot))
            {
                Directory.Delete(state.JailRoot, recursive: true);
            }

            // A preserved workspace's private /tmp is the sandbox's own, never the workspace's, so it goes with the sandbox.
            if (state.LaunchPolicy.PrivateRoot is { } privateRoot && Directory.Exists(privateRoot))
            {
                Directory.Delete(privateRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a locked file does not fail the kill, but a jail left behind is named so it can be found.
            _logger.LogInformation("The sandbox jail {JailRoot} could not be removed: {Reason}", state.JailRoot, exception.Message);
        }
    }

    /// <summary>
    ///     MXC's kill is asynchronous, and on Windows a directory still in use by a dying child (its working directory) survives the delete
    ///     emptied; so the teardown waits for the killed children first, until <paramref name="exitBudget" /> fires.
    /// </summary>
    /// <remarks>
    ///     Synchronous because every caller tears down synchronously; remove and evict do so under the registry lock, terminate-all runs on
    ///     dispose without it. Each call shares one budget across all its jails, so any lock is held at most <see cref="InFlightExitBudget" />.
    /// </remarks>
    private void WaitForExits(List<MxcSandboxChildProcess> killedChildren, CancellationToken exitBudget)
    {
        foreach (var child in killedChildren)
        {
            try
            {
#pragma warning disable MA0045 // forced sync: see the remarks
                _ = child.TryWaitForExitAsync(exitBudget).GetAwaiter().GetResult();
#pragma warning restore MA0045
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A child disposed under the wait (its own scope ended concurrently) is gone either way; the jail delete still runs.
                _logger.LogDebug(exception, "Waiting for a killed sandbox child to exit failed; removing its jail anyway.");
            }
        }
    }
}
