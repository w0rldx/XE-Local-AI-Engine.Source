namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>Configuration for the application-container runtime layer (ADR 0010), engine-owned throughout: nothing here comes from a catalog manifest, a repository or an agent.</summary>
/// <remarks>
///     Deliberately separate from Development Mode's <c>Development:ContainerSandbox</c> section even though both
///     talk to the same daemon. The two consumers have opposite time budgets — a build sandbox is created and torn
///     down inside a request, an application image pull runs for half an hour — so one shared set of timeouts would
///     be sized for the slower one and leave a dead Development Mode preflight hanging for minutes.
/// </remarks>
// A record rather than a plain options class so a caller can derive a variant with `with`. Configuration binding is
// unaffected: it uses the parameterless constructor and the init setters exactly as it would for a class.
public sealed record ContainerRuntimeOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "ContainerRuntime";

    /// <summary>Explicit daemon endpoint, or null to let discovery resolve one: a <c>unix://</c> URI, a <c>npipe://</c> URI or an absolute socket path.</summary>
    /// <remarks>
    ///     A remote transport is refused by the resolver rather than here, because the refusal has to cover a
    ///     <c>DOCKER_HOST</c> that this setting never sees.
    /// </remarks>
    public string? DaemonEndpoint { get; init; }

    /// <summary>
    ///     The oldest Docker Engine API version this layer will use, as <c>major.minor</c>. Compared component-wise,
    ///     not numerically: the daemon's own versions are not decimals, and 1.9 precedes 1.41.
    /// </summary>
    public string MinimumApiVersion { get; init; } = "1.41";

    /// <summary>How long the daemon probe may take. It also floors the client's HTTP request timeout.</summary>
    public int DaemonProbeTimeoutSeconds { get; init; } = 10;

    /// <summary>
    ///     How long one image pull may take. Application images are large and this is the slowest thing a first
    ///     install does, so the pull gets its own deadline rather than inheriting the probe's.
    /// </summary>
    public int PullTimeoutMinutes { get; init; } = 30;

    /// <summary>
    ///     How long a container is given to exit on its own before the daemon kills it. The client's request timeout
    ///     is sized from this, so a graceful stop cannot be cut off by the transport it travels over.
    /// </summary>
    public int StopGracePeriodSeconds { get; init; } = 30;

    /// <summary>
    ///     How long a runtime resolution stays usable before the daemon is probed again. Zero disables the cache,
    ///     which is a legitimate operator choice and therefore an accepted value rather than a validation failure.
    /// </summary>
    public int ResolutionCacheSeconds { get; init; } = 30;
}
