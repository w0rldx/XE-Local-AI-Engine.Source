namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Globalization;
using Microsoft.Extensions.Options;

/// <summary>
///     Fail-closed startup validation for <see cref="ContainerSandboxOptions" />. It rejects, at startup, every
///     configuration that could only produce a container weaker than the §3.8 hardening contract — a root UID, a
///     mutable image tag, a relative mount target, or a scratch area that overlaps the workspace mount.
///     <para>
///         Deliberately NOT gated on "is the container provider selected". A stripped or mistyped configuration must
///         fail loudly whichever provider is in force, and validating unconditionally is what makes the preflight's
///         "the daemon is fine, the configuration is not" case reachable rather than latent.
///     </para>
/// </summary>
internal sealed class ContainerSandboxOptionsValidator : IValidateOptions<ContainerSandboxOptions>
{
    public ValidateOptionsResult Validate(string? name, ContainerSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.Image) && !options.Image.Contains("@sha256:", StringComparison.Ordinal))
        {
            failures.Add($"'{nameof(ContainerSandboxOptions.Image)}' must be digest-pinned (contain '@sha256:'). "
                         + "A tag names whatever the registry last pushed, not the bytes the operator approved (plan D7).");
        }

        // Deliberately NOT rejecting UID/GID 0 here. Whether zero is root depends on the daemon, and this validator
        // runs at startup with no daemon in reach: under a rootless daemon container UID 0 is the invoking user's own
        // unprivileged host account and is the only identity that can use an engine-generated bind mount, while under
        // a rootful one it is host root. A startup rejection would therefore refuse a correct configuration on one
        // machine and accept nothing extra on the other. The check has moved to where the answer is knowable —
        // DockerSandboxRuntimeProvider probes the daemon before it resolves the identity, and refuses UID 0 against a
        // daemon that is not verified rootless. Negative values are still rejected, by the Range data annotation.
        //
        // What IS answerable without a daemon is whether the two halves of the identity agree about which mapping they
        // live in. Under a rootless daemon 0 names the invoking user; pairing a 0 with a non-zero id straddles two
        // different host accounts, so the container would not own what it creates whichever daemon runs it.
        if (options.UserId is not null && options.GroupId is not null && (options.UserId is 0) != (options.GroupId is 0))
        {
            failures.Add($"'{nameof(ContainerSandboxOptions.UserId)}' and '{nameof(ContainerSandboxOptions.GroupId)}' must both be 0 "
                         + "or neither. 0 is meaningful only against a rootless daemon, where it maps to the invoking user's own "
                         + "host account; mixing it with a subordinate id splits the identity across two host accounts and the "
                         + "container would not own what it creates.");
        }

        ValidateMountTarget(nameof(ContainerSandboxOptions.WorkspaceMountTarget), options.WorkspaceMountTarget, failures);
        ValidateMountTarget(nameof(ContainerSandboxOptions.ScratchMountTarget), options.ScratchMountTarget, failures);

        if (Overlaps(options.WorkspaceMountTarget, options.ScratchMountTarget))
        {
            failures.Add($"'{nameof(ContainerSandboxOptions.ScratchMountTarget)}' ('{options.ScratchMountTarget}') and "
                         + $"'{nameof(ContainerSandboxOptions.WorkspaceMountTarget)}' ('{options.WorkspaceMountTarget}') must not overlap — "
                         + "one would shadow the other and the resulting container would not be the one that was verified.");
        }

        if (!TryParseApiVersion(options.MinimumApiVersion, out _))
        {
            failures.Add($"'{nameof(ContainerSandboxOptions.MinimumApiVersion)}' must be 'major.minor' (for example '1.41'), not '{options.MinimumApiVersion}'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    ///     Parses a Docker Engine API version string (<c>major.minor</c>) into a comparable value. Docker reports
    ///     these as decimal-looking strings that are NOT decimals — 1.9 precedes 1.41 — so they are compared
    ///     component-wise as integers. Culture-invariant on purpose: the daemon's wire format is not localized.
    /// </summary>
    internal static bool TryParseApiVersion(string? value, out (int Major, int Minor) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = (major, minor);
        return true;
    }

    /// <summary>Whether <paramref name="observed" /> is at least <paramref name="minimum" />, compared component-wise.</summary>
    internal static bool IsApiVersionAtLeast((int Major, int Minor) observed, (int Major, int Minor) minimum)
    {
        return observed.Major != minimum.Major ? observed.Major > minimum.Major : observed.Minor >= minimum.Minor;
    }

    private static void ValidateMountTarget(string propertyName, string? value, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"'{propertyName}' is required.");
            return;
        }

        // Container paths are POSIX regardless of what the engine host is: the engine may be a native Windows process
        // (D1) while the container is always Linux, so Path.IsPathRooted would answer for the wrong operating system.
        if (!value.StartsWith('/'))
        {
            failures.Add($"'{propertyName}' must be an absolute in-container path starting with '/', not '{value}'.");
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            failures.Add($"'{propertyName}' must not contain '..'.");
        }

        if (value.TrimEnd('/').Length == 0)
        {
            failures.Add($"'{propertyName}' must not be the container root '/'.");
        }
    }

    private static bool Overlaps(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        var left = first.TrimEnd('/');
        var right = second.TrimEnd('/');
        return string.Equals(left, right, StringComparison.Ordinal)
               || left.StartsWith(right + "/", StringComparison.Ordinal)
               || right.StartsWith(left + "/", StringComparison.Ordinal);
    }
}
