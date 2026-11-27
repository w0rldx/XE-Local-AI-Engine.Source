namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A test class is not another file's support host: shared helpers live in a support type of their own.</summary>
/// <remarks>
///     A class named <c>*Tests</c> that declares a <c>[Test]</c> is a host when another file reads a member through
///     <c>NameTests.Member</c>. Reaching into it couples the other file to a test class's private layout and hides the
///     helper from anyone looking for shared support. The four TUnit projects are scanned with comments and literals
///     blanked; today's edges sit in <c>Architecture/TestClassHostAllowlist.txt</c>, shrink-only.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class TestClassHostConventionTests
{
    /// <summary>Non-vacuity floor under today's count: a moved project root would otherwise scan nothing.</summary>
    private const int ScannedFileFloor = 1000;

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "TestClassHostAllowlist.txt");

    private static readonly string[] TestProjects =
    [
        "XE-Local-AI-Engine.Tests/",
        "XE-Local-AI-Engine.AI.Agent.Tests/",
        "XE-Local-AI-Engine.Cli.Tests/",
        "XE-Local-AI-Engine.Client.Persistence.Tests/"
    ];

    private static readonly Lazy<HostScan> Scan = new(ScanTree, isThreadSafe: true);

    [Test]
    public void NoFileUsesATestClassAsItsSupportHost()
    {
        var scan = Scan.Value;

        AssertEx.True(scan.Scanned >= ScannedFileFloor,
            $"Scanned {scan.Scanned} test files, below the non-vacuity floor of {ScannedFileFloor}.");

        var allowed = Allowlist();
        var offenders = scan.Edges.Where(edge => !allowed.Contains(edge)).ToList();

        AssertEx.Empty(offenders,
            "These files read a member of a test class declared in another file (host|consumer). Move the shared member "
            + "into a support type beside the tests (docs/wiki/16-code-conventions.md), and use it from both files:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The shrink-only half: an edge that is gone is deleted.</summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var edges = Scan.Value.Edges.ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist().Where(entry => !edges.Contains(entry)).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(stale,
            "Architecture/TestClassHostAllowlist.txt lists an edge that no longer exists. The list is shrink-only: "
            + "delete the line(s) below." + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    [Arguments("var x = FooTests.Options();", "FooTests")]
    [Arguments("[MethodDataSource(typeof(FooTests), nameof(FooTests.Cases))]", "FooTests")]
    [Arguments("var x = XE_Local_AI_Engine.Tests.FooTests.Options();", "FooTests")]
    [Arguments("var x = global::XE_Local_AI_Engine.Tests.FooTests.Options();", "FooTests")]
    [Arguments("var x = global::FooTests.Options();", "FooTests")]
    [Arguments("var x = BarTests.Options();", "")]
    [Arguments("var x = FooTestsSupport.Options();", "")]
    [Arguments("// var x = FooTests.Options();", "")]
    [Arguments("var s = \"FooTests.Options\";", "")]
    public void TheScan_FlagsOnlyMembersOfAHostDeclaredElsewhere(string consumer, string expected)
    {
        var host = new ScannedFile
        {
            Relative = "XE-Local-AI-Engine.Tests/FooTests.cs",
            Text = SourceCommentStripper.StripCommentsAndLiterals(
                "public sealed class FooTests\n{\n    [Test]\n    public void A() { }\n}"),
        };
        var plain = new ScannedFile
        {
            Relative = "XE-Local-AI-Engine.Tests/BarTests.cs",
            Text = SourceCommentStripper.StripCommentsAndLiterals("public sealed class BarTests { }"),
        };
        var reader = new ScannedFile
        {
            Relative = "XE-Local-AI-Engine.Tests/Other.cs",
            Text = SourceCommentStripper.StripCommentsAndLiterals(consumer),
        };

        var edges = EdgesIn([host, plain, reader]);

        AssertEx.Equal(expected.Length == 0 ? string.Empty : $"{expected}|XE-Local-AI-Engine.Tests/Other.cs",
            string.Join(",", edges));
    }

    private static HostScan ScanTree()
    {
        var files = EnforcedSourceFiles.All()
                                       .Where(file => TestProjects.Any(project => file.Relative.StartsWith(project,
                                           StringComparison.Ordinal)))
                                       .Select(file => new ScannedFile
                                       {
                                           Relative = file.Relative,
                                           Text = SourceCommentStripper.StripCommentsAndLiterals(File.ReadAllText(file.Path)),
                                       })
                                       .ToList();

        return new HostScan { Scanned = files.Count, Edges = EdgesIn(files) };
    }

    /// <summary>Every <c>host|consumer</c> pair, sorted, where the consumer file does not declare the host itself.</summary>
    private static IReadOnlyList<string> EdgesIn(IReadOnlyList<ScannedFile> files)
    {
        var declaring = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var declaresATest = TestAttribute().IsMatch(file.Text);

            foreach (Match declaration in TestClassDeclaration().Matches(file.Text))
            {
                var name = declaration.Groups["name"].Value;

                if (!declaring.TryGetValue(name, out var owners))
                {
                    owners = new HashSet<string>(StringComparer.Ordinal);
                    declaring[name] = owners;
                }

                owners.Add(file.Relative);

                if (declaresATest)
                {
                    hosts.Add(name);
                }
            }
        }

        var edges = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            foreach (Match use in MemberAccess().Matches(file.Text))
            {
                var name = use.Groups["host"].Value;

                if (hosts.Contains(name) && !declaring[name].Contains(file.Relative))
                {
                    edges.Add($"{name}|{file.Relative}");
                }
            }
        }

        return [.. edges];
    }

    private static HashSet<string> Allowlist() =>
        File.ReadLines(AllowlistPath)
            .Select(raw => raw.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\[\s*Test\s*[\],(]")]
    private static partial Regex TestAttribute();

    [GeneratedRegex(@"\bclass\s+(?<name>\w+Tests)\b")]
    private static partial Regex TestClassDeclaration();

    // A namespace or global:: qualifier in front reads the same host.
    [GeneratedRegex(@"(?<!\w)(?<host>\w+Tests)\s*\.\s*\w")]
    private static partial Regex MemberAccess();

    /// <summary>One test file: its repository-relative path and its stripped text.</summary>
    private sealed class ScannedFile
    {
        public required string Relative { get; init; }

        public required string Text { get; init; }
    }

    /// <summary>One walk of the test projects: files scanned and every host edge found.</summary>
    private sealed class HostScan
    {
        public required int Scanned { get; init; }

        public required IReadOnlyList<string> Edges { get; init; }
    }
}
