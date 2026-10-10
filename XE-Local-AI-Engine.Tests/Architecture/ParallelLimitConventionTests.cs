namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Keeps the parallel cap of eight on both heavy test assemblies, and nothing overriding it: a class-level limiter
///     or <c>ClearParallelLimiter</c> takes precedence and reopens TUnit's 4 × cores default.
/// </summary>
[Category(TestCategories.Unit)]
public sealed partial class ParallelLimitConventionTests
{
    private static readonly string[] CappedProjects = ["XE-Local-AI-Engine.Tests", "XE-Local-AI-Engine.Client.Persistence.Tests"];

    [GeneratedRegex(@"\[assembly:\s*ParallelLimiter<[A-Za-z_.]*AssemblyParallelLimit>\]")]
    private static partial Regex AssemblyCap();

    [GeneratedRegex(@"public int Limit => 8;")]
    private static partial Regex LimitOfEight();

    [GeneratedRegex(@"ParallelLimiter(Attribute)?<")]
    private static partial Regex ClassOrMethodLimiter();

    [Test]
    [Arguments("XE-Local-AI-Engine.Tests")]
    [Arguments("XE-Local-AI-Engine.Client.Persistence.Tests")]
    public void TheAssembly_CapsParallelTestsAtEight(string project)
    {
        var source = SourceCommentStripper.StripComments(File.ReadAllText(RepositoryPaths.Combine(project, "AssemblyParallelLimit.cs")));

        AssertEx.True(AssemblyCap().IsMatch(source), $"{project}/AssemblyParallelLimit.cs lost its [assembly: ParallelLimiter<AssemblyParallelLimit>]: a bare host run would start 4 × cores tests at once.");
        AssertEx.True(LimitOfEight().IsMatch(source), $"{project}/AssemblyParallelLimit.cs no longer caps at eight; raise it only with a new RSS measurement.");
    }

    [Test]
    public void NoTest_OverridesTheAssemblyCap()
    {
        var offenders = new List<string>();
        foreach (var project in CappedProjects)
        {
            foreach (var file in Directory.EnumerateFiles(RepositoryPaths.Combine(project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepositoryPaths.Root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.EndsWith("/AssemblyParallelLimit.cs", StringComparison.Ordinal) || relative.EndsWith("/ParallelLimitConventionTests.cs", StringComparison.Ordinal))
                {
                    continue;
                }

                var source = SourceCommentStripper.StripComments(File.ReadAllText(file));
                if (ClassOrMethodLimiter().IsMatch(source) || source.Contains("ClearParallelLimiter", StringComparison.Ordinal))
                {
                    offenders.Add(relative);
                }
            }
        }

        AssertEx.Empty(offenders, $"A class or method limiter takes precedence over the assembly cap. Offenders: {string.Join(", ", offenders)}");
    }
}
