namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Request to create a new AgentHome sandbox or attach to the existing one for the same
///     <see cref="SandboxAttachKey" />. Provider-neutral: resource and network preferences are
///     expressed as neutral values that a provider applies only when it advertises the matching capability.
/// </summary>
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
    ///     How strongly this sandbox's commands are separated from the host filesystem. Defaults to
    ///     <see cref="SandboxIsolationMode.None" />, which is exactly the behaviour every existing caller already has:
    ///     opting in is the only way to change anything.
    ///     <para>
    ///         Unlike <see cref="ResourceLimits" /> this is NOT a preference a provider may quietly drop. A provider
    ///         that cannot deliver it rejects the request with <see cref="SandboxCapabilityNotSupportedException" />,
    ///         because a caller asking for a filesystem boundary is asking for the one thing it must not be wrong
    ///         about.
    ///     </para>
    /// </summary>
    public SandboxIsolationMode Isolation { get; init; } = SandboxIsolationMode.None;

    /// <summary>
    ///     Host trees the isolated sandbox must be able to READ, bound read-only at their own canonical paths. Only
    ///     meaningful with <see cref="SandboxIsolationMode.Filesystem" />; supplying them without it is rejected,
    ///     because under the non-isolated mode the sandbox can already read the whole host filesystem and honouring
    ///     the list would suggest a narrowing that did not happen.
    ///     <para>
    ///         Engine-generated only, and deliberately narrow: name the interpreter tree, never the directory that
    ///         also holds the scratch, the cache or the lock state a later command would inherit.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string>? ReadOnlyTrees { get; init; }

    /// <summary>
    ///     The value every numeric-library thread-count variable is pinned to inside an isolated sandbox. Left unset
    ///     it is one. It exists because those libraries size their pools from the HOST's core count, which is not what
    ///     the sandbox's CPU quota allows — an unpinned BLAS starts a thread per host core and then thrashes inside a
    ///     fraction of one.
    /// </summary>
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
    ///     Optional per-sandbox ceiling, in bytes, on how much THIS sandbox's commands may leave in its jail directory.
    ///     <see langword="null" /> (the default) means "inherit the node-wide ceiling"; a supplied value must be
    ///     greater than zero.
    ///     <para>
    ///         It may only TIGHTEN. A provider applies <c>min(node-wide ceiling, this value)</c>, so a request asking
    ///         for more than the node allows still gets the node's number, and a request can never re-enable a watchdog
    ///         the operator disabled. That asymmetry is the point: the node-wide value
    ///         (<c>LocalContainerOptions.MaxJailDiskBytes</c>) is the OPERATOR's ceiling on what any sandbox on the box
    ///         may write, and a caller — whose request shape is influenced by whatever workload it serves — must not be
    ///         able to widen it. Naming a smaller number only shrinks the blast radius of one runaway command.
    ///     </para>
    ///     <para>
    ///         <b>It is a CREATE-TIME ceiling, with future-command tightening on attach.</b> The sandbox this request
    ///         creates carries it for its whole life. A later <c>CreateOrAttachAsync</c> under the same attach key does
    ///         not create a second sandbox, so it cannot re-specify the ceiling — but if it names a STRICTER one, the
    ///         live sandbox's ceiling is lowered to it, atomically and permanently, and applies to every command
    ///         started from then on. A looser value, or none at all, changes nothing; a disabled node-wide watchdog
    ///         stays disabled. Commands already running keep the ceiling they started under: they were launched
    ///         against a budget, and moving the line mid-write would kill a process for bytes that were within the
    ///         rules when it wrote them.
    ///     </para>
    ///     <para>
    ///         A caller whose attach key is unique per call (<c>ComputeToolGateway</c> keys its jail per invocation)
    ///         therefore only ever exercises the create-time path — every call is a fresh sandbox with its own ceiling.
    ///     </para>
    ///     <para>
    ///         Like <see cref="ResourceLimits" /> this is a preference, applied by providers that implement a
    ///         jail-growth watchdog and ignored by those that do not. Ignoring it is safe by construction: the field
    ///         can only ever ask for LESS than what the provider would otherwise enforce, so no guarantee is downgraded
    ///         by dropping it.
    ///     </para>
    /// </summary>
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
    ///     Additional engine-generated mounts the sandbox needs beyond <see cref="TrustedHostWorkspace" /> — the
    ///     per-task HOME, temp, package-cache and tool-state roots a build writes to, and any file the sandbox must see
    ///     read-only.
    ///     <para>
    ///         Engine-generated <em>only</em>. Nothing here may be derived from a registered repository: a repository is
    ///         a tree the agent can write, and repository-supplied mount configuration is rejected wholesale
    ///         because a repository that could name a mount could name the daemon socket.
    ///     </para>
    ///     <para>
    ///         What is NOT here is as load-bearing as what is. The workspace control manifest must be
    ///         unreachable from inside any sandbox, and the cheapest way to satisfy it is to mount the named
    ///         subdirectories of a control-state root rather than the root itself — so a caller lists
    ///         <c>&lt;runtime&gt;/home</c> and its siblings, never <c>&lt;runtime&gt;</c>.
    ///     </para>
    /// </summary>
    public IReadOnlyList<SandboxMount>? Mounts { get; init; }
}
