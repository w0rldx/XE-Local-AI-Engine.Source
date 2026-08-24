namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
///     Names — and recognises — the transient systemd scope every isolated command runs in.
///     <para>
///         The unit name is the whole termination story. A jail's processes live in their own PID namespace, so the
///         engine cannot see them, and the pid it holds is <c>setsid</c>'s, three execs away from the workload. What
///         it CAN do is name the cgroup they are all in and ask the user manager to signal it, which reaches every
///         process in the scope including one that deliberately detached. That only works if the name is decided
///         BEFORE the launch and recorded where a later run can find it, which is why this is a generated identifier
///         rather than something read back from <c>systemd-run</c>'s output.
///     </para>
///     <para>
///         The shape is deliberately narrow — <c>xe-&lt;role&gt;-&lt;32 hex&gt;.scope</c> — because the startup sweep
///         reaps by pattern. A loose prefix match would let the sweep kill a unit some other tool happened to name
///         <c>xe-something</c>; matching the exact generated shape means the sweep can only ever target units this
///         engine created.
///     </para>
/// </summary>
internal static partial class SandboxScopeUnit
{
    /// <summary>The role used when a caller names none.</summary>
    public const string DefaultRole = "sandbox";

    /// <summary>The <c>systemctl</c> glob that lists candidate units for the startup sweep.</summary>
    public const string ListPattern = "xe-*.scope";

    /// <summary>Builds a fresh unit name for one command.</summary>
    public static string Create(string? role)
    {
        var sanitized = Sanitize(role);

        return string.Create(CultureInfo.InvariantCulture, $"xe-{sanitized}-{Guid.NewGuid():N}.scope");
    }

    /// <summary>
    ///     <see langword="true" /> when <paramref name="unitName" /> has the exact shape <see cref="Create" />
    ///     produces. The startup sweep signals nothing that fails this.
    /// </summary>
    public static bool IsEngineOwned(string? unitName)
    {
        return !string.IsNullOrEmpty(unitName) && EngineOwnedUnit().IsMatch(unitName);
    }

    private static string Sanitize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return DefaultRole;
        }

        var builder = new StringBuilder(role.Length);
        foreach (var character in role.Where(char.IsAsciiLetterOrDigit))
        {
            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.Length == 0 ? DefaultRole : builder.ToString();
    }

    [GeneratedRegex(@"^xe-[a-z0-9]+-[0-9a-f]{32}\.scope$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EngineOwnedUnit();
}
