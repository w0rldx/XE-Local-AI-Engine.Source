namespace XE_Local_AI_Engine.Client.Services.Containers;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Fail-closed startup validation for <see cref="ContainerRuntimeOptions" />, registered with <c>ValidateOnStart</c> so a mistyped value stops the node.</summary>
/// <remarks>
///     The alternative is a container operation that times out weeks later. Every failure names the full
///     configuration key, not the property: an operator reading a start-up failure wants the line to edit, and
///     <c>'MinimumApiVersion' is invalid</c> does not say which of several sections holds it.
/// </remarks>
internal sealed class ContainerRuntimeOptionsValidator : IValidateOptions<ContainerRuntimeOptions>
{
    /// <summary>The accepted range for <see cref="ContainerRuntimeOptions.DaemonProbeTimeoutSeconds" />.</summary>
    internal const int MinimumProbeTimeoutSeconds = 1;

    /// <summary>The largest probe timeout accepted: past two minutes a dead socket stops looking like a dead socket.</summary>
    internal const int MaximumProbeTimeoutSeconds = 120;

    /// <summary>The accepted range for <see cref="ContainerRuntimeOptions.PullTimeoutMinutes" />.</summary>
    internal const int MinimumPullTimeoutMinutes = 1;

    /// <summary>The largest pull deadline accepted.</summary>
    internal const int MaximumPullTimeoutMinutes = 240;

    /// <summary>The smallest value accepted for the two second-valued windows; zero is a real choice, not a mistake.</summary>
    internal const int MinimumWindowSeconds = 0;

    /// <summary>The largest value accepted for the two second-valued windows.</summary>
    internal const int MaximumWindowSeconds = 600;

    public ValidateOptionsResult Validate(string? name, ContainerRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // The sandbox validator's parser, not a second one: both read the same daemon wire format, and two copies
        // of "major.minor with non-negative parts" is how one of them quietly stops agreeing with the other.
        if (!ContainerSandboxOptionsValidator.TryParseApiVersion(options.MinimumApiVersion, out _))
        {
            failures.Add($"'{Key(nameof(ContainerRuntimeOptions.MinimumApiVersion))}' must be 'major.minor' with both parts "
                         + $"non-negative (for example '1.41'), not '{options.MinimumApiVersion}'.");
        }

        ValidateRange(nameof(ContainerRuntimeOptions.DaemonProbeTimeoutSeconds),
            options.DaemonProbeTimeoutSeconds,
            MinimumProbeTimeoutSeconds,
            MaximumProbeTimeoutSeconds,
            failures);
        ValidateRange(nameof(ContainerRuntimeOptions.PullTimeoutMinutes),
            options.PullTimeoutMinutes,
            MinimumPullTimeoutMinutes,
            MaximumPullTimeoutMinutes,
            failures);
        ValidateRange(nameof(ContainerRuntimeOptions.StopGracePeriodSeconds),
            options.StopGracePeriodSeconds,
            MinimumWindowSeconds,
            MaximumWindowSeconds,
            failures);
        ValidateRange(nameof(ContainerRuntimeOptions.ResolutionCacheSeconds),
            options.ResolutionCacheSeconds,
            MinimumWindowSeconds,
            MaximumWindowSeconds,
            failures);

        // Blank means "discover one", the shipped default. A value that is neither a URI nor an absolute path fails,
        // because discovery would ignore it and reach a daemon the operator did not name.
        if (!string.IsNullOrWhiteSpace(options.DaemonEndpoint)
            && !Uri.IsWellFormedUriString(options.DaemonEndpoint, UriKind.Absolute)
            && !Path.IsPathRooted(options.DaemonEndpoint))
        {
            failures.Add($"'{Key(nameof(ContainerRuntimeOptions.DaemonEndpoint))}' must be an absolute URI (for example "
                         + $"'unix:///var/run/docker.sock') or an absolute socket path, not '{options.DaemonEndpoint}'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRange(string property, int value, int minimum, int maximum, List<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"'{Key(property)}' must be between {minimum} and {maximum}, not {value}."));
        }
    }

    private static string Key(string property)
    {
        return ContainerRuntimeOptions.SectionName + ":" + property;
    }
}
