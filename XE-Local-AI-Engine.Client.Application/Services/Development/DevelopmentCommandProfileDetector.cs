namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     What detection found in a registered repository, before the operator confirms it.
/// </summary>
/// <param name="ProfileId">The code-owned profile id this repository looks like.</param>
/// <param name="BuildTarget">The repository-relative solution or project file, null for <c>generic-git</c>.</param>
/// <param name="Candidates">
///     Every build target found, so the operator can pick a different one when a repository has more than one. Bounded,
///     because a large repository can contain hundreds of project files and this list crosses an API boundary.
/// </param>
public sealed record DevelopmentProfileDetection(
    string ProfileId,
    string? BuildTarget,
    IReadOnlyList<string> Candidates);

/// <summary>
///     Public so tests can substitute it, matching <c>IDevelopmentRepositoryBindingService</c> and
///     <c>IDevelopmentCoordinator</c>: this assembly is not strong-named, so Castle DynamicProxy cannot proxy an
///     internal interface. The implementation stays internal.
/// </summary>
public interface IDevelopmentCommandProfileDetector
{
    DevelopmentProfileDetection Detect(string repositoryRoot);
}

/// <summary>
///     Detects which code-owned command profile fits a registered repository.
///     <para>
///         Detection is a <em>proposal</em>. It runs against the trusted host repository root at registration time and
///         its result is shown to the operator for confirmation; nothing here is authoritative on its own. That matters
///         because a repository the agent can write must never be able to choose its own build commands.
///     </para>
///     <para>
///         A repository with no recognizable .NET build system resolves to
///         <see cref="DevelopmentCommandProfileCatalog.GenericGit" />, whose validation profile is the whitespace check
///         alone. That is deliberately a visible, operator-confirmed downgrade rather than a silent one: the profile is
///         named in the confirmation step and stamped on every artifact the gate produces, so a report that only
///         checked whitespace says so.
///     </para>
/// </summary>
internal sealed class DevelopmentCommandProfileDetector : IDevelopmentCommandProfileDetector
{
    /// <summary>
    ///     How deep to look for a project file. Solutions are expected at the repository root; a single-project
    ///     repository commonly nests one or two levels (<c>src/Lib/Lib.csproj</c>). Deeper than this and the right
    ///     answer is a solution file, not a guess.
    /// </summary>
    private const int MaxProjectSearchDepth = 3;

    private const int MaxCandidates = 50;

    public DevelopmentProfileDetection Detect(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repositoryRoot);

        // A solution wins over a project file even when both exist: it is the target that builds and tests the whole
        // repository, which is what the validation gate is supposed to measure.
        var solutions = EnumerateRelative(canonical, "*.slnx", 1)
                        .Concat(EnumerateRelative(canonical, "*.sln", 1))
                        .Take(MaxCandidates)
                        .ToArray();
        if (solutions.Length > 0)
        {
            return new DevelopmentProfileDetection(DevelopmentCommandProfileCatalog.DotnetSlnx, solutions[0], solutions);
        }

        var projects = EnumerateRelative(canonical, "*.csproj", MaxProjectSearchDepth)
                       .Take(MaxCandidates)
                       .ToArray();
        return projects.Length > 0
            ? new DevelopmentProfileDetection(DevelopmentCommandProfileCatalog.DotnetCsproj, projects[0], projects)
            : new DevelopmentProfileDetection(DevelopmentCommandProfileCatalog.GenericGit, BuildTarget: null, []);
    }

    /// <summary>
    ///     Enumerates matching files as repository-relative forward-slash paths, ordered so detection is deterministic
    ///     for a given tree — a non-deterministic first candidate would make the confirmed profile depend on filesystem
    ///     enumeration order, and therefore make its digest unstable across machines.
    /// </summary>
    private static IEnumerable<string> EnumerateRelative(string root, string pattern, int maxDepth)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = maxDepth > 1,
            MaxRecursionDepth = Math.Max(maxDepth - 1, 0),
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(root, pattern, options)
                        .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                        .Where(static relative => !relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
                                                  && !relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                                                  && !relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(static relative => relative.Count(static character => character == '/'))
                        .ThenBy(static relative => relative, StringComparer.Ordinal);
    }
}
