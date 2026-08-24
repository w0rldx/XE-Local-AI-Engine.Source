namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Request to create a new AgentHome sandbox or attach to the existing one for the same
///     <see cref="SandboxAttachKey" />. Provider-neutral: resource and network preferences are
///     expressed as neutral values that a provider applies only when it advertises the matching capability.
/// </summary>
public sealed record SandboxCreateRequest
{
    private readonly long? _maxJailDiskBytes;

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
    ///     Optional per-sandbox ceiling, in bytes, on how far a command may grow THIS sandbox's jail directory.
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
