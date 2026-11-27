namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A test uses the shared double instead of declaring a private copy of it.</summary>
/// <remarks>
///     The scan reads comment- and literal-stripped source of the four TUnit projects and flags a class that implements
///     <c>INodeSqliteKeyHolder</c> or <c>IAgentHomeIdentityProvider</c>, a class named after a shared clock, or a
///     disposable temp-directory class. Copies the shared double cannot replace sit in
///     <c>Architecture/SharedTestDoubleAllowlist.txt</c>, shrink-only, each with its reason.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class SharedTestDoubleConventionTests
{
    /// <summary>Non-vacuity floor under today's count: a moved project root would otherwise scan nothing.</summary>
    private const int ScannedFileFloor = 1000;

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "SharedTestDoubleAllowlist.txt");

    private static readonly string[] TestProjects =
    [
        "XE-Local-AI-Engine.Tests/",
        "XE-Local-AI-Engine.AI.Agent.Tests/",
        "XE-Local-AI-Engine.Cli.Tests/",
        "XE-Local-AI-Engine.Client.Persistence.Tests/"
    ];

    /// <summary>The shared doubles that live inside a scanned project; every other shared double is in Client.Testing.</summary>
    private static readonly HashSet<string> SharedHomes = new(StringComparer.Ordinal)
    {
        "XE-Local-AI-Engine.Tests/Testing/FixedAgentHomeIdentityProvider.cs|FixedAgentHomeIdentityProvider",
        "XE-Local-AI-Engine.Tests/Testing/ImmediateTimerTimeProvider.cs|ImmediateTimerTimeProvider"
    };

    private static readonly HashSet<string> InterfacesWithASharedDouble = new(StringComparer.Ordinal) { "INodeSqliteKeyHolder", "IAgentHomeIdentityProvider" };

    private static readonly HashSet<string> ClockNames = new(StringComparer.Ordinal) { "ManualTimeProvider", "FixedTimeProvider", "SteppingClock", "SteppingTimeProvider", "ImmediateTimerTimeProvider" };

    private static readonly HashSet<string> TempDirectoryNames = new(StringComparer.Ordinal) { "TempDir", "TempDirectory" };

    private static readonly Lazy<CopyScan> Scan = new(ScanTree, isThreadSafe: true);

    [Test]
    public void NoTestDeclaresAPrivateCopyOfASharedDouble()
    {
        var scan = Scan.Value;

        AssertEx.True(scan.Scanned >= ScannedFileFloor,
            $"Scanned {scan.Scanned} test files, below the non-vacuity floor of {ScannedFileFloor}.");

        var allowed = Allowlist().Keys.ToHashSet(StringComparer.Ordinal);
        var offenders = scan.Copies.Where(copy => !allowed.Contains(copy)).ToList();

        AssertEx.Empty(offenders,
            "These test files declare a private copy of a shared double. Use the shared one (FixedNodeSqliteKeyHolder, "
            + "FixedAgentHomeIdentityProvider, ManualTimeProvider, SteppingTimeProvider, ImmediateTimerTimeProvider, "
            + "TempDirectory); see "
            + "docs/wiki/17-writing-tests.md, \"Shared doubles first\". A copy the shared double cannot replace goes in "
            + "Architecture/SharedTestDoubleAllowlist.txt with its reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The shrink-only half: an entry whose copy is gone.</summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var copies = Scan.Value.Copies.ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist().Keys.Where(entry => !copies.Contains(entry)).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(stale,
            "Architecture/SharedTestDoubleAllowlist.txt lists a copy that no longer exists. The list is shrink-only: "
            + "delete the line(s) below." + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    [Arguments("private sealed class FixedKeyHolder : INodeSqliteKeyHolder { }", "FixedKeyHolder")]
    [Arguments("internal sealed class Stub : IDisposable, IAgentHomeIdentityProvider\n{ }", "Stub")]
    [Arguments("private sealed class ManualTimeProvider : TimeProvider { }", "ManualTimeProvider")]
    [Arguments("private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { }", "FixedTimeProvider")]
    [Arguments("private sealed class SteppingClock : TimeProvider { }", "SteppingClock")]
    [Arguments("private sealed class SteppingTimeProvider : TimeProvider { }", "SteppingTimeProvider")]
    [Arguments("private sealed class ImmediateTimerTimeProvider : TimeProvider { }", "ImmediateTimerTimeProvider")]
    [Arguments("private sealed class TempDir : IDisposable { }", "TempDir")]
    [Arguments("private sealed class TempDirectory : IDisposable { }", "TempDirectory")]
    [Arguments("private sealed class TempDirectory { }", "")]
    [Arguments("private sealed class DueTimeRecordingTimeProvider : TimeProvider { }", "")]
    [Arguments("private sealed class Holder : INodeSqliteKeyHolderFactory { }", "")]
    [Arguments("// private sealed class SteppingClock : TimeProvider { }", "")]
    [Arguments("var text = \"class TempDir : IDisposable {\";", "")]
    public void TheScan_FlagsOnlyCopiesOfASharedDouble(string source, string expected)
    {
        var found = string.Join(",", CopiesIn(SourceCommentStripper.StripCommentsAndLiterals(source)));

        AssertEx.Equal(expected, found);
    }

    private static IEnumerable<string> CopiesIn(string strippedSource)
    {
        foreach (Match declaration in ClassDeclaration().Matches(strippedSource))
        {
            var name = declaration.Groups["name"].Value;
            var bases = declaration.Groups["bases"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (bases.Any(InterfacesWithASharedDouble.Contains)
                || ClockNames.Contains(name)
                || (TempDirectoryNames.Contains(name) && bases.Contains("IDisposable", StringComparer.Ordinal)))
            {
                yield return name;
            }
        }
    }

    private static CopyScan ScanTree()
    {
        var scanned = 0;
        var copies = new List<string>();

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            if (!TestProjects.Any(project => relative.StartsWith(project, StringComparison.Ordinal)))
            {
                continue;
            }

            scanned++;
            var source = SourceCommentStripper.StripCommentsAndLiterals(File.ReadAllText(path));
            copies.AddRange(CopiesIn(source).Select(name => $"{relative}|{name}").Where(copy => !SharedHomes.Contains(copy)));
        }

        return new CopyScan
        {
            Scanned = scanned,
            Copies = [.. copies.Order(StringComparer.Ordinal)]
        };
    }

    /// <summary>Entries keyed by <c>path|type</c>; a line without a reason is malformed.</summary>
    private static Dictionary<string, string> Allowlist()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(AllowlistPath).Select(raw => raw.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            var fields = line.Split('|');
            AssertEx.True(fields.Length == 3 && fields.All(field => field.Trim().Length > 0),
                $"Malformed line in Architecture/SharedTestDoubleAllowlist.txt (want path|type|reason): {line}");
            entries.Add($"{fields[0]}|{fields[1]}", fields[2]);
        }

        return entries;
    }

    // The base list runs to the opening brace; a primary constructor's parameter list sits between the name and it.
    [GeneratedRegex(@"\bclass\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^>{]*>)?\s*(?:\([^)]*\))?\s*(?::(?<bases>[^{]*))?\{")]
    private static partial Regex ClassDeclaration();

    /// <summary>One walk of the test projects: files scanned and every copy found, as <c>path|type</c>.</summary>
    private sealed class CopyScan
    {
        public required int Scanned { get; init; }

        public required IReadOnlyList<string> Copies { get; init; }
    }
}
