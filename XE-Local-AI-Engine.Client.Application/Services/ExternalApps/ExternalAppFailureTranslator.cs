namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Where in a pipeline a failure happened. The translator keys on this rather than on daemon prose.</summary>
public enum ExternalAppFailurePhase
{
    /// <summary>Resolving the container runtime and checking the manifest's requirements against it.</summary>
    Resolution = 0,

    /// <summary>Building the deployment plan: token resolution, ordering, mount collisions.</summary>
    Plan = 1,

    /// <summary>Creating directories and materialising catalog assets.</summary>
    Storage = 2,

    /// <summary>Pulling images.</summary>
    Pull = 3,

    /// <summary>Creating the instance network.</summary>
    Network = 4,

    /// <summary>Creating containers.</summary>
    Create = 5,

    /// <summary>Starting containers.</summary>
    Start = 6,

    /// <summary>Reading a container back and verifying it against the policy.</summary>
    Verify = 7,

    /// <summary>Waiting for a service to run or to become healthy.</summary>
    Wait = 8
}

/// <summary>
///     One failure as it is persisted and shown: a category the UI can act on and a summary safe to store.
/// </summary>
/// <remarks>
///     The summary is deliberately content-free — category prose, a service name and, for resources, requested
///     against available figures. Never a variable value, never a host path, never a raw daemon message. The failure
///     is written to a database column and rendered in a browser, and a daemon message can carry the environment it
///     failed on.
/// </remarks>
public sealed class ExternalAppFailure
{
    public required ExternalAppFailureCategory Category { get; init; }

    public required string Summary { get; init; }
}

/// <summary>
///     Turns the exception a pipeline phase threw into a failure category. It keys on the PHASE, because daemon
///     prose changes between Docker releases and a translator matching on message text silently degrades to
///     <see cref="ExternalAppFailureCategory.Unknown" /> after an upgrade nobody connected to it.
/// </summary>
public static class ExternalAppFailureTranslator
{
    /// <summary>
    ///     Translates one failure. <paramref name="plannedHostPorts" /> are the host ports the attempt chose: a
    ///     create or start failure is <see cref="ExternalAppFailureCategory.PortUnavailable" /> only when one of them
    ///     is no longer bindable, which is a re-probe rather than a message match.
    /// </summary>
    public static ExternalAppFailure Translate(ExternalAppFailurePhase phase,
        Exception exception,
        IReadOnlyList<int>? plannedHostPorts = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // A policy refusal is a policy violation wherever it happened: the runtime layer raises it for a request
        // that was well formed but that what the daemon already holds makes unsafe.
        if (exception is ContainerPolicyException)
        {
            return new ExternalAppFailure { Category = ExternalAppFailureCategory.PolicyViolation, Summary = "The container runtime refused the request on policy grounds." };
        }

        if (exception is ContainerRuntimeUnavailableException)
        {
            return new ExternalAppFailure { Category = ExternalAppFailureCategory.RuntimeUnavailable, Summary = "The container runtime is not available." };
        }

        // ProbeFailed is the type's default, i.e. "no status was stated"; anything else is the daemon telling us it
        // cannot serve this engine at all, which is a runtime problem rather than this application's.
        if (exception is DockerRuntimeException { Status: not DockerDaemonPreflightStatus.ProbeFailed })
        {
            return new ExternalAppFailure { Category = ExternalAppFailureCategory.RuntimeUnavailable, Summary = "The container daemon is not usable." };
        }

        if (phase is ExternalAppFailurePhase.Create or ExternalAppFailurePhase.Start
            && plannedHostPorts is { Count: > 0 }
            && plannedHostPorts.Any(static port => !ExternalAppPortAllocator.IsBindable(port)))
        {
            return new ExternalAppFailure
            {
                Category = ExternalAppFailureCategory.PortUnavailable,
                Summary = "A host port this application needs was taken by another process."
            };
        }

        return phase switch
        {
            ExternalAppFailurePhase.Resolution => new ExternalAppFailure
            {
                Category = ExternalAppFailureCategory.RuntimeIncompatible,
                Summary = "The container runtime does not offer everything this application requires."
            },
            ExternalAppFailurePhase.Plan => new ExternalAppFailure
            {
                Category = ExternalAppFailureCategory.ConfigurationMissing,
                Summary = "This application cannot be configured as the catalog describes it."
            },
            ExternalAppFailurePhase.Storage => new ExternalAppFailure
            {
                Category = ExternalAppFailureCategory.StorageError,
                Summary = "This application's storage could not be prepared."
            },
            ExternalAppFailurePhase.Pull => new ExternalAppFailure
            {
                Category = ExternalAppFailureCategory.ImagePullFailed,
                Summary = "An image this application needs could not be pulled."
            },
            ExternalAppFailurePhase.Verify => new ExternalAppFailure
            {
                Category = ExternalAppFailureCategory.PolicyViolation,
                Summary = "A container did not match the policy it was created with."
            },
            ExternalAppFailurePhase.Wait => new ExternalAppFailure
            {
                Category = ExternalAppFailureCategory.HealthCheckFailed,
                Summary = "A service did not become ready."
            },
            _ => new ExternalAppFailure { Category = ExternalAppFailureCategory.Unknown, Summary = "This application could not be started." }
        };
    }

    /// <summary>The refusal for a manifest requiring a capability the runtime does not offer, named without prose from the daemon.</summary>
    public static ExternalAppFailure ForMissingCapabilities(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);

        return new ExternalAppFailure
        {
            Category = ExternalAppFailureCategory.RuntimeIncompatible,
            Summary = $"The container runtime does not offer: {string.Join(", ", missing)}."
        };
    }

    /// <summary>The refusal for <c>gpu: required</c>. No GPU device request is representable at this layer, by design.</summary>
    public static ExternalAppFailure ForGpuRequired()
    {
        return new ExternalAppFailure
        {
            Category = ExternalAppFailureCategory.GpuNotSupported,
            Summary = "This application requires a GPU inside its container, which this engine does not pass through."
        };
    }

    /// <summary>
    ///     The summary a read-back verification failure carries: the service name and how many checks it failed, and
    ///     nothing else.
    /// </summary>
    /// <remarks>
    ///     The violations themselves are deliberately NOT quoted. They are composed against what the daemon reported,
    ///     and several of them — an undeclared mount, a mount backed by the wrong directory — name a host path. This
    ///     summary is persisted to a column and rendered in a browser, which is the one place a host path must not
    ///     reach. The full list goes to the node log at the point it is found.
    /// </remarks>
    public static ExternalAppFailure ForViolations(string serviceName, IReadOnlyList<string> violations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(violations);

        if (violations.Count == 0)
        {
            return new ExternalAppFailure { Category = ExternalAppFailureCategory.PolicyViolation, Summary = $"Service '{serviceName}' failed verification." };
        }

        return new ExternalAppFailure
        {
            Category = ExternalAppFailureCategory.PolicyViolation,
            Summary = string.Create(CultureInfo.InvariantCulture,
                $"Service '{serviceName}' failed {violations.Count} policy check(s); the node log names them.")
        };
    }
}
