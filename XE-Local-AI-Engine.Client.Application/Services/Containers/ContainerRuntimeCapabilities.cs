namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     What a container runtime can do, in the exact vocabulary a catalog manifest's <c>requires[]</c> uses.
///     <para>
///         The names and the check live together, in this layer, rather than the names living in the catalog and the
///         check in the application layer: two statements of one vocabulary disagree the first time one of them is
///         edited, and the failure mode is an application declaring a requirement nothing evaluates.
///     </para>
/// </summary>
public sealed record ContainerRuntimeCapabilities
{
    /// <summary>The runtime can create, start, stop, inspect and remove containers.</summary>
    public required bool Containers { get; init; }

    /// <summary>The runtime can create and remove labelled bridge networks with DNS between attached containers.</summary>
    public required bool Networks { get; init; }

    /// <summary>The runtime can bind host directories into a container.</summary>
    public required bool BindStorage { get; init; }

    /// <summary>The runtime can publish a container port on a loopback host address.</summary>
    public required bool LoopbackPortPublishing { get; init; }

    /// <summary>The runtime can run a container healthcheck and report its verdict.</summary>
    public required bool HealthChecks { get; init; }

    /// <summary>The runtime can apply a restart policy that survives the engine being stopped.</summary>
    public required bool RestartPolicies { get; init; }

    /// <summary>The runtime can read a container's logs.</summary>
    public required bool Logs { get; init; }

    /// <summary>The runtime can pull a digest-pinned image.</summary>
    public required bool ImagePull { get; init; }

    /// <summary>
    ///     The runtime can pass GPU devices into a container. Always <see langword="false" />: ADR 0010 makes GPU
    ///     device requests a non-goal, and the flag exists so a manifest asking for one is refused by name rather than
    ///     by an unrecognised requirement.
    /// </summary>
    public required bool GpuDevices { get; init; }

    /// <summary>Nothing is available — the shape reported when no runtime answered.</summary>
    public static ContainerRuntimeCapabilities None { get; } = new()
    {
        Containers = false,
        Networks = false,
        BindStorage = false,
        LoopbackPortPublishing = false,
        HealthChecks = false,
        RestartPolicies = false,
        Logs = false,
        ImagePull = false,
        GpuDevices = false
    };

    /// <summary>A ready Docker daemon: everything except GPU devices.</summary>
    public static ContainerRuntimeCapabilities DockerReady { get; } = new()
    {
        Containers = true,
        Networks = true,
        BindStorage = true,
        LoopbackPortPublishing = true,
        HealthChecks = true,
        RestartPolicies = true,
        Logs = true,
        ImagePull = true,
        GpuDevices = false
    };

    /// <summary>
    ///     The nine camelCase names a manifest may put in <c>requires[]</c>, and the only ones. Ordered as the record
    ///     declares them so a reader can check the two lists against each other by eye.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "containers",
        "networks",
        "bindStorage",
        "loopbackPortPublishing",
        "healthChecks",
        "restartPolicies",
        "logs",
        "imagePull",
        "gpuDevices"
    ];

    /// <summary>
    ///     The requested capability names this runtime does not offer, in the order they were asked for.
    ///     <para>
    ///         A name this vocabulary does not define counts as missing rather than as satisfied. A runtime cannot
    ///         honour a requirement it does not understand, and treating the unknown as met is how a manifest written
    ///         against a later schema installs silently and fails at run time.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> FindMissing(IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(required);

        return [.. required.Where(name => !Offers(name))];
    }

    private bool Offers(string name)
    {
        return name switch
        {
            "containers" => Containers,
            "networks" => Networks,
            "bindStorage" => BindStorage,
            "loopbackPortPublishing" => LoopbackPortPublishing,
            "healthChecks" => HealthChecks,
            "restartPolicies" => RestartPolicies,
            "logs" => Logs,
            "imagePull" => ImagePull,
            "gpuDevices" => GpuDevices,
            _ => false
        };
    }
}
