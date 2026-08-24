namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     One prepared isolated launch: the descriptors and sealed memory files it needs, and the argument vector that
///     references them by number.
///     <para>
///         This is the IMPURE half of the isolated launch — it opens things — kept apart from
///         <see cref="SandboxIsolatedChain" />, which decides what the vector says. Splitting them is what makes the
///         chain assertable byte for byte in a unit test while the opening, which cannot be, stays small enough to
///         read.
///     </para>
///     <para>
///         <b>Disposal frees descriptors, not the child.</b> The descriptors must stay open until the process has been
///         started (the child inherits copies at that moment and keeps them through all three execs); closing them
///         afterwards is correct and required, because a leaked descriptor per command would exhaust the engine's
///         descriptor table over a long session.
///     </para>
/// </summary>
internal sealed class SandboxIsolationLaunch : IDisposable
{
    // The jail subdirectories the chain assumes: HOME inside the sandbox and the jail-backed /tmp. Both live under the
    // jail so everything the workload accumulates is inside the one tree the disk watchdog walks.
    private const string HomeDirectoryName = "home";
    private const string TempDirectoryName = ".tmp";

    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly List<IDisposable> _resources;

    private SandboxIsolationLaunch(IReadOnlyList<string> chain, string scopeUnitName, List<IDisposable> resources)
    {
        Chain = chain;
        ScopeUnitName = scopeUnitName;
        _resources = resources;
    }

    /// <summary>The full argument vector; element 0 is the executable to start.</summary>
    public IReadOnlyList<string> Chain { get; }

    /// <summary>The transient scope's unit name — the kill authority for this command.</summary>
    public string ScopeUnitName { get; }

    /// <summary>
    ///     Opens every descriptor the chain needs and renders it. Throws
    ///     <see cref="SandboxIsolationUnavailableException" /> when any ingredient cannot be prepared; there is no
    ///     partial success and no pathname fallback.
    /// </summary>
    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Every opened resource is registered in the disposal list on the same statement; the catch disposes the whole list.")]
    public static SandboxIsolationLaunch Create(SandboxFilesystemIsolation isolation,
        SandboxIsolationLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(isolation);
        ArgumentNullException.ThrowIfNull(request);

        var resources = new List<IDisposable>();
        try
        {
            // 0700 on the jail itself, not only on what is created under it. The jail is the workload's single
            // writable surface and the descriptor opener refuses anything looser, so a default-umask 0755 directory
            // would turn a perfectly capable host into an unexplained launch failure.
            var jailRoot = EnsurePrivateDirectory(Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.JailRoot)));
            // HOME inside the sandbox is /work/home; this is the host directory behind it.
            _ = EnsurePrivateDirectory(Path.Combine(jailRoot, HomeDirectoryName));
            var temp = EnsurePrivateDirectory(Path.Combine(jailRoot, TempDirectoryName));

            // The jail itself must be 0700: it is the one writable surface the workload has, and a mode anyone else
            // could write to would make the boundary pointless from the outside in.
            var jailDescriptor = Track(resources, SandboxTrustedDescriptorOpener.Open(jailRoot, requirePrivateMode: true));
            var tempDescriptor = Track(resources, SandboxTrustedDescriptorOpener.Open(temp, requirePrivateMode: true));

            var readOnlyTrees = new List<SandboxIsolatedTreeBinding>(request.ReadOnlyTrees.Count);
            foreach (var tree in request.ReadOnlyTrees)
            {
                if (!SandboxIsolatedChain.CanBindReadOnlyTree(Path.TrimEndingDirectorySeparator(Path.GetFullPath(tree))))
                {
                    throw new SandboxIsolationUnavailableException(
                        $"the read-only tree '{tree}' lies under a mount point the sandbox chain owns ({string.Join(", ", SandboxIsolatedChain.ReservedMountPoints)}), where it would be shadowed rather than visible");
                }

                // Read-only trees are engine-owned but not necessarily private: a provisioned interpreter is 0755 so
                // it can be executed. What matters is that nobody ELSE can write it, which the opener enforces.
                var descriptor = Track(resources, SandboxTrustedDescriptorOpener.Open(tree, requirePrivateMode: false));
                readOnlyTrees.Add(new SandboxIsolatedTreeBinding(descriptor.FileDescriptor, descriptor.Path));
            }

            var passwd = Track(resources, SandboxSealedMemoryFile.Create("xe-passwd", SandboxSyntheticEtc.BuildPasswd(isolation.UserId, isolation.GroupId)));
            var group = Track(resources, SandboxSealedMemoryFile.Create("xe-group", SandboxSyntheticEtc.BuildGroup(isolation.GroupId)));
            var nameServiceSwitch = Track(resources, SandboxSealedMemoryFile.Create("xe-nsswitch", SandboxSyntheticEtc.BuildNameServiceSwitch()));
            var hosts = Track(resources, SandboxSealedMemoryFile.Create("xe-hosts", SandboxSyntheticEtc.BuildHosts()));

            passwd.RewindForLaunch();
            group.RewindForLaunch();
            nameServiceSwitch.RewindForLaunch();
            hosts.RewindForLaunch();

            var unitName = SandboxScopeUnit.Create(request.Role);
            var inputs = new SandboxIsolatedChainInputs
            {
                SetsidPath = isolation.SetsidPath,
                SystemdRunPath = isolation.SystemdRunPath,
                BwrapPath = isolation.BwrapPath,
                ScopeUnitName = unitName,
                RuntimeMaxSeconds = request.RuntimeMaxSeconds,
                UserId = isolation.UserId,
                GroupId = isolation.GroupId,
                UsrMergeEntries = isolation.UsrMergeEntries,
                PasswdDescriptor = passwd.FileDescriptor,
                GroupDescriptor = group.FileDescriptor,
                NameServiceSwitchDescriptor = nameServiceSwitch.FileDescriptor,
                HostsDescriptor = hosts.FileDescriptor,
                JailDescriptor = jailDescriptor.FileDescriptor,
                JailTempDescriptor = tempDescriptor.FileDescriptor,
                ReadOnlyTrees = readOnlyTrees,
                ResourceLimits = request.ResourceLimits,
                ThreadLimit = request.ThreadLimit
            };

            return new SandboxIsolationLaunch(SandboxIsolatedChain.Render(inputs, request.Executable, request.Arguments), unitName, resources);
        }
        catch
        {
            foreach (var resource in resources)
            {
                resource.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        foreach (var resource in _resources)
        {
            resource.Dispose();
        }

        _resources.Clear();
    }

    private static T Track<T>(List<IDisposable> resources, T resource)
        where T : IDisposable
    {
        resources.Add(resource);

        return resource;
    }

    private static string EnsurePrivateDirectory(string path)
    {
        try
        {
            var directory = Directory.CreateDirectory(path);
            if (OperatingSystem.IsLinux())
            {
                // Created 0700 explicitly rather than left to the umask: the descriptor opener REQUIRES 0700 on the
                // jail and its temp, and a loose umask would otherwise turn a working host into an unexplained
                // capability failure.
                File.SetUnixFileMode(path, PrivateDirectoryMode);
            }

            return directory.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SandboxIsolationUnavailableException($"the jail subdirectory '{path}' could not be created", exception);
        }
    }
}

/// <summary>The per-command inputs to <see cref="SandboxIsolationLaunch.Create" />.</summary>
internal sealed record SandboxIsolationLaunchRequest
{
    /// <summary>The sandbox's jail directory, which becomes <c>/work</c> inside.</summary>
    public required string JailRoot { get; init; }

    public required string Executable { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Host trees bound read-only at their own canonical paths.</summary>
    public IReadOnlyList<string> ReadOnlyTrees { get; init; } = [];

    public SandboxResourceLimits? ResourceLimits { get; init; }

    public int ThreadLimit { get; init; } = 1;

    /// <summary>Wall-clock ceiling the user manager enforces on the scope.</summary>
    public required long RuntimeMaxSeconds { get; init; }

    /// <summary>The role segment of the unit name, for readability in <c>systemctl</c> output.</summary>
    public string? Role { get; init; }
}
