namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A test in this project finds a repository file through <see cref="RepositoryPaths" />, never by walking up from
///     the test binary or the working directory on its own.
/// </summary>
/// <remarks>
///     Worktrees live under <c>.tmp/worktrees/</c> inside the main checkout, so a walk that stops at the first directory
///     holding the file it wants passes the worktree root and reads the main checkout's copy: the test goes green on a
///     file the branch never touched. <see cref="RepositoryPaths" /> stops at the nearest solution file. The scan
///     (comments and literals stripped) flags a file that names a seed (binary, working directory, assembly location)
///     and steps a variable to its own parent, however the seed reaches the walk.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class RepositoryRootWalkUpGuardTests
{
    private const string ProjectPrefix = "XE-Local-AI-Engine.Tests/";
    private const string HelperPath = "XE-Local-AI-Engine.Tests/Testing/RepositoryPaths.cs";

    private static readonly Regex Seed = new(
        @"AppContext\.BaseDirectory|Directory\.GetCurrentDirectory\(\)|Environment\.CurrentDirectory|\bAssembly\s*\.\s*Location\b|Get(?:Executing|Entry|Calling)Assembly\(\)\s*\.\s*Location\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // `d = d.Parent`, `d = d?.Parent`, `d = Directory.GetParent(d…)`, `p = Path.GetDirectoryName(p)`: a step that repeats.
    private static readonly Regex ParentStep = new(
        @"\b(\w+)\s*=\s*(?:\1\s*!?\s*\??\.\s*Parent\b|Directory\.GetParent\(\s*\1\b|Path\.GetDirectoryName\(\s*\1\b)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Files allowed a seeded walk, each with the reason it cannot use <see cref="RepositoryPaths" />.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        [HelperPath] = "the helper itself",
        ["XE-Local-AI-Engine.Tests/ExternalApps/Catalog/ExternalAppCatalogSeed.cs"] =
            "stops at the nearest .git and returns null outside a checkout, so the dist parity test skips visibly instead of failing",
    };

    [Test]
    public void NoTestWalksUpFromTheBinaryOrTheWorkingDirectory_OutsideRepositoryPaths()
    {
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var file in EnforcedSourceFiles.All())
        {
            if (!file.Relative.StartsWith(ProjectPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;
            if (!Allowed.ContainsKey(file.Relative) && IsSeededWalkUp(File.ReadAllText(file.Path)))
            {
                offenders.Add(file.Relative);
            }
        }

        // Non-vacuity: the project holds well over a thousand files; the allowlist test proves the shape still matches.
        AssertEx.True(scanned > 1000, $"Expected the scan to cover the test project; it read {scanned} files.");
        AssertEx.Empty(offenders,
            "These files walk up from the test binary or the working directory; from a worktree that walk can reach the "
            + $"main checkout. Resolve the path through RepositoryPaths instead: {string.Join(", ", offenders)}");
    }

    [Test]
    public void EveryAllowedFile_StillWalksUpFromASeed()
    {
        foreach (var (path, reason) in Allowed)
        {
            var absolute = RepositoryPaths.Combine(path);
            AssertEx.True(File.Exists(absolute) && IsSeededWalkUp(File.ReadAllText(absolute)),
                $"Stale allowlist entry '{path}' ({reason}): the file is gone or no longer walks up from a seed; delete the entry.");
        }
    }

    [Test]
    [Arguments("var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null) { directory = directory.Parent; }")]
    [Arguments("foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }) { for (var d = new DirectoryInfo(seed); d is not null; d = d.Parent) { } }")]
    [Arguments("var start = AppContext.BaseDirectory; var d = new DirectoryInfo(start); while (d != null) { d = d?.Parent; }")]
    [Arguments("Find(AppContext.BaseDirectory); static string? Find(string start) { for (var d = new DirectoryInfo(start); d is not null; d = d.Parent) { } return null; }")]
    [Arguments("var dir = Environment.CurrentDirectory; while (dir is not null) { dir = Directory.GetParent(dir)?.FullName; }")]
    [Arguments("var path = typeof(Marker).Assembly.Location; while (path is not null) { path = Path.GetDirectoryName(path); }")]
    [Arguments("var path = Assembly.GetExecutingAssembly().Location; while (path is not null) { path = Path.GetDirectoryName(path); }")]
    public void IsSeededWalkUp_RecognisesEverySeedAndStepShape(string source)
    {
        AssertEx.True(IsSeededWalkUp(source), $"Expected a seeded walk-up: {source}");
    }

    [Test]
    [Arguments("var path = Path.Combine(AppContext.BaseDirectory, \"Fixtures\", name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);")]
    [Arguments("Expect(AppContext.BaseDirectory); nvm.Parent!.Parent!.Delete(recursive: true);")]
    [Arguments("static string? Find(string start) { for (var d = new DirectoryInfo(start); d is not null; d = d.Parent) { } return null; }")]
    public void IsSeededWalkUp_IgnoresAFixedClimbAndAWalkWithNoSeedInTheFile(string source)
    {
        AssertEx.False(IsSeededWalkUp(source), $"Expected no seeded walk-up: {source}");
    }

    private static bool IsSeededWalkUp(string source)
    {
        var code = SourceCommentStripper.StripCommentsAndLiterals(source);
        return ParentStep.IsMatch(code) && Seed.IsMatch(code);
    }
}
