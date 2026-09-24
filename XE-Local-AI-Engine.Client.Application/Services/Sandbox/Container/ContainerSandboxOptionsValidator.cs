namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Globalization;
using Microsoft.Extensions.Options;

/// <summary>
///     Fail-closed startup validation for <see cref="ContainerSandboxOptions" />: it rejects every configuration that could only produce a
///     container weaker than the Docker hardening contract.
/// </summary>
/// <remarks>
///     A root UID, a mutable image tag, a relative mount target, or a scratch area overlapping the workspace mount. Deliberately NOT gated
///     on whether the container provider is selected: a stripped or mistyped configuration must fail loudly whichever provider is in
///     force, and validating unconditionally is what makes the preflight's "the daemon is fine, the configuration is not" case reachable.
/// </remarks>
internal sealed class ContainerSandboxOptionsValidator : IValidateOptions<ContainerSandboxOptions>
{
    public ValidateOptionsResult Validate(string? name, ContainerSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.Image) && !options.Image.Contains("@sha256:", StringComparison.Ordinal))
        {
            failures.Add($"'{nameof(ContainerSandboxOptions.Image)}' must be digest-pinned (contain '@sha256:'). "
                         + "A tag names whatever the registry last pushed, not the bytes the operator approved.");
        }

        // Deliberately NOT rejecting UID/GID 0: whether zero is root depends on the daemon this startup validator cannot reach, so the
        // check moved to the provider. What IS answerable here is agreement — pairing 0 with a non-zero id straddles two host accounts.
        if (options.UserId is not null && options.GroupId is not null && (options.UserId is 0) != (options.GroupId is 0))
        {
            failures.Add($"'{nameof(ContainerSandboxOptions.UserId)}' and '{nameof(ContainerSandboxOptions.GroupId)}' must both be 0 "
                         + "or neither. 0 is meaningful only against a rootless daemon, where it maps to the invoking user's own "
                         + "host account; mixing it with a subordinate id splits the identity across two host accounts and the "
                         + "container would not own what it creates.");
        }

        ValidateMountTarget(nameof(ContainerSandboxOptions.WorkspaceMountTarget), options.WorkspaceMountTarget, failures);
        ValidateMountTarget(nameof(ContainerSandboxOptions.ScratchMountTarget), options.ScratchMountTarget, failures);
        ValidateMountTarget(nameof(ContainerSandboxOptions.TempMountTarget), options.TempMountTarget, failures);

        // An N-way sweep, not pairwise calls: two targets need one comparison and three need three, and adding a fourth by hand is how a
        // pair gets missed. FindOverlap is shared with the provider's mount broker, which sweeps an unbounded generated list.
        if (FindOverlap([
                new ContainerMountTarget
                {
                    Name = nameof(ContainerSandboxOptions.WorkspaceMountTarget),
                    Path = options.WorkspaceMountTarget
                },
                new ContainerMountTarget
                {
                    Name = nameof(ContainerSandboxOptions.ScratchMountTarget),
                    Path = options.ScratchMountTarget
                },
                new ContainerMountTarget
                {
                    Name = nameof(ContainerSandboxOptions.TempMountTarget),
                    Path = options.TempMountTarget
                }
            ]) is { } collision)
        {
            failures.Add($"'{collision.Second.Name}' ('{collision.Second.Path}') and '{collision.First.Name}' ('{collision.First.Path}') must not "
                         + "overlap — one would shadow the other and the resulting container would not be the one that was verified.");
        }

        if (!TryParseApiVersion(options.MinimumApiVersion, out _))
        {
            failures.Add($"'{nameof(ContainerSandboxOptions.MinimumApiVersion)}' must be 'major.minor' (for example '1.41'), not '{options.MinimumApiVersion}'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Parses a Docker Engine API version string (<c>major.minor</c>) into a comparable value.</summary>
    /// <remarks>
    ///     Docker reports these as decimal-looking strings that are NOT decimals — 1.9 precedes 1.41 — so they are compared
    ///     component-wise as integers. Culture-invariant on purpose: the daemon's wire format is not localized.
    /// </remarks>
    internal static bool TryParseApiVersion(string? value, out DockerApiVersion version)
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

        version = new DockerApiVersion(major, minor);
        return true;
    }

    /// <summary>Whether <paramref name="observed" /> is at least <paramref name="minimum" />, compared component-wise.</summary>
    internal static bool IsApiVersionAtLeast(DockerApiVersion observed, DockerApiVersion minimum)
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
        // while the container is always Linux, so Path.IsPathRooted would answer for the wrong operating system.
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

    /// <summary>
    ///     Sweeps every pair in <paramref name="targets" /> and returns the first collision, or <see langword="null" /> when no target
    ///     shadows another.
    /// </summary>
    /// <remarks>
    ///     Two container paths collide when equal or when one is an ancestor of the other: a mount at an ancestor hides everything the
    ///     descendant was to expose, and the container the daemon reads back is then not the one that was verified. Shared rather than
    ///     duplicated because the callers sweep different sets — startup sweeps the configured targets, the provider sweeps those PLUS
    ///     every engine-generated target — and the rule must be the same in both or a mount startup would refuse becomes reachable.
    /// </remarks>
    internal static ContainerMountOverlap? FindOverlap(IReadOnlyList<ContainerMountTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        for (var outer = 0; outer < targets.Count; outer++)
        {
            for (var inner = outer + 1; inner < targets.Count; inner++)
            {
                if (Overlaps(targets[outer].Path, targets[inner].Path))
                {
                    return new ContainerMountOverlap
                    {
                        First = targets[outer],
                        Second = targets[inner]
                    };
                }
            }
        }

        return null;
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
