namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     An opaque reference to a live AgentHome sandbox. Carries the provider name, the
///     provider's sandbox/container id, the <see cref="SandboxAttachKey" /> it was created/attached under, its
///     creation time, and the manifest version in force. Immutable: liveness is owned by the provider, so an
///     operation against a killed sandbox throws <see cref="SandboxHandleInvalidException" /> rather than reading a
///     stale flag off the handle.
/// </summary>
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

    /// <summary>
    ///     Every engine-generated mount this sandbox carries, as the provider RESOLVED it — including the trusted host
    ///     workspace. This is the answer to "what is this host path called inside the sandbox?", and it is the only
    ///     honest place to ask: the requested <see cref="SandboxMount.SandboxPath" /> is a preference, and the process
    ///     provider necessarily ignores it because a host child sees host paths.
    /// </summary>
    public IReadOnlyList<SandboxMountBinding> Mounts { get; init; } = [];

    /// <summary>
    ///     Translates a host path into the path that names the same bytes inside this sandbox, or
    ///     <see langword="null" /> when no mount covers it.
    ///     <para>
    ///         Matches the mount root itself and anything beneath it, longest root first, so a nested mount wins over
    ///         the workspace it sits inside. Deliberately returns null rather than falling back to the host path: a
    ///         caller that handed a container a host path would produce a command that fails deep inside a build with a
    ///         "directory not found" that names a path the container has never heard of, which is far harder to read
    ///         than a refusal at composition time.
    ///     </para>
    /// </summary>
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
