namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Names — and recognises — the transient systemd scope every isolated command runs in.</summary>
/// <remarks>
///     The unit name is the whole termination story: a jail's processes live in their own PID namespace and the pid the engine holds is
///     <c>setsid</c>'s, so naming the cgroup and asking the user manager to signal it is what reaches every process, one that deliberately
///     detached included. That works only if the name is decided BEFORE the launch and recorded where a later run can find it, hence a
///     generated identifier rather than something read back from <c>systemd-run</c>. The shape is deliberately narrow,
///     <c>xe-&lt;role&gt;-&lt;32 hex&gt;.scope</c>, so the startup sweep can only ever target units this engine created.
/// </remarks>
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
