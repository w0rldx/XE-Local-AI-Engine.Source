namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>An opaque reference to a live AgentHome sandbox.</summary>
/// <remarks>
///     Immutable: liveness is owned by the provider, so an operation against a killed sandbox throws
///     <see cref="SandboxHandleInvalidException" /> rather than reading a stale flag off the handle.
/// </remarks>
public sealed record SandboxHandle
{
    /// <summary>The provider that owns this sandbox.</summary>
    public required string ProviderName { get; init; }

    /// <summary>The provider's sandbox/container id.</summary>
    public required string SandboxId { get; init; }

    /// <summary>The attach key the sandbox was created or attached under.</summary>
    public required SandboxAttachKey AttachKey { get; init; }

    /// <summary>When the sandbox was created (from the provider's <see cref="TimeProvider" />).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The AgentHome manifest version in force for this sandbox.</summary>
    public required int ManifestVersion { get; init; }

    /// <summary>The isolation the provider ACTUALLY DELIVERED for this sandbox — not what the create request asked for.</summary>
    /// <remarks>
    ///     A caller whose behaviour depends on a filesystem boundary must branch on this rather than on its own request: the request is a
    ///     preference a provider may refuse, and the two only agree because the process provider rejects an unmeetable one fail-closed.
    ///     Reading it off the handle makes "did I get the boundary?" a fact about the sandbox in hand, not an inference about the host.
    ///     <see cref="SandboxIsolationMode.None" /> is the default, so a provider that does not isolate reports the honest answer without
    ///     opting in.
    /// </remarks>
    public SandboxIsolationMode Isolation { get; init; } = SandboxIsolationMode.None;

    /// <summary>
    ///     Every engine-generated mount this sandbox carries, as the provider RESOLVED it, including the trusted host workspace.
    /// </summary>
    /// <remarks>
    ///     The only honest answer to "what is this host path called inside the sandbox?": the requested
    ///     <see cref="SandboxMount.SandboxPath" /> is a preference, and the process provider necessarily ignores it because a host child
    ///     sees host paths.
    /// </remarks>
    public IReadOnlyList<SandboxMountBinding> Mounts { get; init; } = [];

    /// <summary>
    ///     The directory this sandbox is rooted at, named the way a COMMAND INSIDE IT sees the path: where a command with no working
    ///     directory starts, and what a sandbox-relative path resolves against.
    /// </summary>
    /// <remarks>
    ///     A SANDBOX path, not necessarily a host path — the container provider reports its workspace mount target, which names nothing on
    ///     the host. Use it to compose a path for the CHILD, never to open a file from engine code; the process provider identity-maps its
    ///     jail, so there the two coincide. <see langword="null" /> when the provider has no such directory (the deterministic fake is
    ///     virtual): read null as "this provider cannot serve me" rather than substituting a path — <c>ComputeToolGateway</c> refuses, its
    ///     scratch directory having to sit inside the jail the disk watchdog meters.
    /// </remarks>
    public string? WorkingRoot { get; init; }

    /// <summary>
    ///     Translates a host path into the path that names the same bytes inside this sandbox, or <see langword="null" /> when no mount
    ///     covers it.
    /// </summary>
    /// <remarks>
    ///     Matches the mount root itself and anything beneath it, longest root first, so a nested mount wins over the workspace it sits
    ///     inside. It deliberately returns null rather than falling back to the host path: handing a container a host path produces a
    ///     command that fails deep inside a build with a "directory not found" naming a path the container never heard of, which is far
    ///     harder to read than a refusal at composition time.
    /// </remarks>
    public string? TryResolveSandboxPath(string hostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostPath));
        foreach (var mount in Mounts.OrderByDescending(static mount => mount.HostPath.Length))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount.HostPath));
            if (string.Equals(canonical, root, PathComparison))
            {
                return mount.SandboxPath;
            }

            var prefix = root + Path.DirectorySeparatorChar;
            if (canonical.StartsWith(prefix, PathComparison))
            {
                var relative = canonical[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
                return mount.SandboxPath.TrimEnd('/') + "/" + relative;
            }
        }

        return null;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
