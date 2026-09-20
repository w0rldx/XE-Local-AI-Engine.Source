namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Request to create a new AgentHome sandbox, or attach to the existing one for the same <see cref="SandboxAttachKey" />.
/// </summary>
/// <remarks>
///     Provider-neutral: resource and network preferences are neutral values a provider applies only where it advertises the matching
///     capability.
/// </remarks>
public sealed record SandboxCreateRequest
{
    private readonly long? _maxJailDiskBytes;
    private readonly int? _threadLimit;

    /// <summary>Owner/node-scoped identity for the sandbox.</summary>
    public required SandboxAttachKey AttachKey { get; init; }

    /// <summary>The runtime profile to create the sandbox with (e.g. <c>"dotnet-agent-home"</c>).</summary>
    public required string RuntimeProfile { get; init; }

    /// <summary>Optional resource ceiling; applied only when the provider supports resource limits.</summary>
    public SandboxResourceLimits? ResourceLimits { get; init; }

    /// <summary>Requested network posture; defaults to no network.</summary>
    public SandboxNetworkPolicy NetworkPolicy { get; init; } = SandboxNetworkPolicy.None;

    /// <summary>Optional provider-neutral labels/metadata to associate with the sandbox.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>
    ///     How strongly this sandbox's commands are separated from the host filesystem; the default
    ///     <see cref="SandboxIsolationMode.None" /> is what every existing caller already has, so opting in is the only way to change it.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="ResourceLimits" /> this is NOT a preference a provider may quietly drop: one that cannot deliver it rejects
    ///     the request with <see cref="SandboxCapabilityNotSupportedException" />, because a caller asking for a filesystem boundary is
    ///     asking for the one thing it must not be wrong about.
    /// </remarks>
    public SandboxIsolationMode Isolation { get; init; } = SandboxIsolationMode.None;

    /// <summary>
    ///     Host trees the isolated sandbox must be able to READ, bound read-only at their own canonical paths.
    /// </summary>
    /// <remarks>
    ///     Only meaningful with <see cref="SandboxIsolationMode.Filesystem" />; supplying them without it is rejected, because the
    ///     non-isolated mode already reads the whole host filesystem and honouring the list would suggest a narrowing that did not happen.
    ///     Engine-generated only and deliberately narrow: name the interpreter tree, never a directory that also holds the scratch, the
    ///     cache or the lock state a later command would inherit.
    /// </remarks>
    public IReadOnlyList<string>? ReadOnlyTrees { get; init; }

    /// <summary>
    ///     The value every numeric-library thread-count variable is pinned to inside an isolated sandbox; unset it is one.
    /// </summary>
    /// <remarks>
    ///     Those libraries size their pools from the HOST's core count, not from what the sandbox's CPU quota allows — an unpinned BLAS
    ///     starts a thread per host core and then thrashes inside a fraction of one.
    /// </remarks>
    public int? ThreadLimit
    {
        get => _threadLimit;
        init
        {
            if (value is { } limit && limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    limit,
                    "A sandbox thread limit must be greater than zero; omit it to pin every numeric library to a single thread.");
            }

            _threadLimit = value;
        }
    }

    /// <summary>
    ///     Optional per-sandbox ceiling, in bytes, on how much THIS sandbox's commands may leave in its jail directory;
    ///     <see langword="null" /> inherits the node-wide ceiling and a supplied value must be greater than zero.
    /// </summary>
    /// <remarks>
    ///     TIGHTEN-ONLY: a provider applies <c>min(node-wide ceiling, this value)</c>, so asking for more than the operator allows still
    ///     gets the node's number and a request can never re-enable a watchdog the operator disabled. It is a CREATE-TIME ceiling the
    ///     sandbox carries for life, except that a later attach naming a STRICTER one lowers it atomically and permanently for every
    ///     command started after — commands already running keep the budget they were launched against. Like <see cref="ResourceLimits" />
    ///     it is a preference, and dropping it is safe because it can only ever ask for LESS than the provider already enforces.
    /// </remarks>
    public long? MaxJailDiskBytes
    {
        get => _maxJailDiskBytes;
        init
        {
            if (value is { } ceiling && ceiling <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    ceiling,
                    "A per-sandbox jail disk ceiling must be greater than zero; omit it to inherit the node-wide ceiling.");
            }

            _maxJailDiskBytes = value;
        }
    }

    /// <summary>
    ///     Optional engine-managed trusted host workspace. Providers must either confine the sandbox to this root and
    ///     preserve it on kill/restart, or reject the request fail-closed.
    /// </summary>
    public SandboxTrustedHostWorkspace? TrustedHostWorkspace { get; init; }

    /// <summary>
    ///     Additional engine-generated mounts the sandbox needs beyond <see cref="TrustedHostWorkspace" /> — the per-task HOME, temp,
    ///     package-cache and tool-state roots a build writes to, and any file the sandbox must see read-only.
    /// </summary>
    /// <remarks>
    ///     Engine-generated ONLY: nothing here may be derived from a registered repository, which is a tree the agent can write, and a
    ///     repository that could name a mount could name the daemon socket. What is NOT here is as load-bearing: the workspace control
    ///     manifest must be unreachable from inside any sandbox, so a caller lists the named subdirectories of a control-state root —
    ///     <c>&lt;runtime&gt;/home</c> and its siblings — never <c>&lt;runtime&gt;</c> itself.
    /// </remarks>
    public IReadOnlyList<SandboxMount>? Mounts { get; init; }
}
