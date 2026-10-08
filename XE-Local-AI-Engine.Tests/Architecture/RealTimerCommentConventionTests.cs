namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A test file that waits on a real timer says why, in a <c>// real-timer:</c> comment (AGENTS.md).</summary>
/// <remarks>
///     A sleep that waits for, or rules out, an event is the flake this rule prevents; a real timer is allowed only
///     with its reason written down. The scan reads comment- and literal-stripped source of the three TUnit test
///     projects. An infinite delay waits for cancellation, not time, so it does not count. Files that predate the
///     rule sit in <c>Architecture/RealTimerAllowlist.txt</c>, shrink-only. Style: <c>docs/wiki/17-writing-tests.md</c>.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class RealTimerCommentConventionTests
{
    private const string Marker = "// real-timer:";

    /// <summary>Non-vacuity floors under today's counts: a moved project root would otherwise scan nothing.</summary>
    private const int ScannedFileFloor = 1000;

    private const int MarkedFileFloor = 5;

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "RealTimerAllowlist.txt");

    private static readonly string[] TestProjects =
    [
        "XE-Local-AI-Engine.Tests/",
        "XE-Local-AI-Engine.AI.Agent.Tests/",
        "XE-Local-AI-Engine.Client.Persistence.Tests/"
    ];

    private static readonly Lazy<TimerScan> Scan = new(ScanTree, isThreadSafe: true);

    [Test]
    public void EveryRealTimerWait_CarriesItsReason()
    {
        var scan = Scan.Value;

        AssertEx.True(scan.Scanned >= ScannedFileFloor,
            $"Scanned {scan.Scanned} test files, below the non-vacuity floor of {ScannedFileFloor}.");
        AssertEx.True(scan.Marked >= MarkedFileFloor,
            $"Found {scan.Marked} files carrying '{Marker}', below the floor of {MarkedFileFloor}: the marker search broke.");

        var allowed = Allowlist();
        var offenders = scan.Unmarked.Where(file => !allowed.Contains(file)).ToList();

        AssertEx.Empty(offenders,
            "A test that waits on a real timer (Task.Delay / Thread.Sleep with a finite argument) must say why in a "
            + $"'{Marker} <reason>' comment in the same file. Better: wait on a gate the test controls, a "
            + "FakeTimeProvider, or AssertEx.EventuallyAsync. Files:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The shrink-only half: an entry whose file now carries the marker, stopped waiting, or is gone.</summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var unmarked = Scan.Value.Unmarked.ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist().Where(entry => !unmarked.Contains(entry)).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(stale,
            "Architecture/RealTimerAllowlist.txt lists a file that now carries its '// real-timer:' reason, no longer "
            + "waits on a real timer, or no longer exists. The list is shrink-only: delete the line(s) below."
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    [Arguments("await Task.Delay(50);", true)]
    [Arguments("Thread.Sleep(10);", true)]
    [Arguments("await Task.Delay(\n    TimeSpan.FromSeconds(1), ct);", true)]
    [Arguments("await Task.Delay(PollPause, ct);", true)]
    [Arguments("await Task.Delay(Timeout.Infinite, ct);", false)]
    [Arguments("await Task.Delay(Timeout.InfiniteTimeSpan, ct);", false)]
    [Arguments("await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);", false)]
    [Arguments("await Task.Delay(-1, ct);", false)]
    [Arguments("await Task.Delay( Timeout.InfiniteTimeSpan, ct);", false)]
    [Arguments("await Task.Delay(\n    Timeout.InfiniteTimeSpan, ct);", false)]
    [Arguments("await Task.Delay( -1, ct);", false)]
    [Arguments("await Task.Delay( TimeSpan.FromSeconds(1));", true)]
    [Arguments("await Task.Delay(Timeout.Infinite + 101);", true)]
    [Arguments("await Task.Delay(Timeout.InfiniteTimeSpan / 2, ct);", true)]
    [Arguments("// await Task.Delay(50);", false)]
    [Arguments("var text = \"Task.Delay(50)\";", false)]
    public void TheScan_CountsOnlyFiniteRealTimerWaits(string source, bool counts)
    {
        AssertEx.Equal(counts, WaitsOnARealTimer(source));
    }

    private static bool WaitsOnARealTimer(string source) =>
        FiniteWait().IsMatch(SourceCommentStripper.StripCommentsAndLiterals(source));

    private static TimerScan ScanTree()
    {
        var scanned = 0;
        var marked = 0;
        var unmarked = new List<string>();

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            if (!TestProjects.Any(project => relative.StartsWith(project, StringComparison.Ordinal)))
            {
                continue;
            }

            scanned++;
            var source = File.ReadAllText(path);

            if (source.Contains(Marker, StringComparison.Ordinal))
            {
                marked++;
            }
            else if (WaitsOnARealTimer(source))
            {
                unmarked.Add(relative);
            }
        }

        return new TimerScan
        {
            Scanned = scanned,
            Marked = marked,
            Unmarked = [.. unmarked.Order(StringComparer.Ordinal)]
        };
    }

    /// <summary>Repository-relative paths, read from the repository rather than the test output directory.</summary>
    private static HashSet<string> Allowlist() =>
        File.ReadLines(AllowlistPath)
            .Select(raw => raw.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    // Whitespace sits inside the lookahead (outside, `\s*` backtracks to zero); the whole first argument must be the
    // infinite value, so `Timeout.Infinite + 101` still counts.
    [GeneratedRegex(@"\b(?:Task\.Delay|Thread\.Sleep)\((?!\s*(?:(?:System\.Threading\.)?Timeout\.Infinite(?:TimeSpan)?|-1)\s*[,)])")]
    private static partial Regex FiniteWait();

    /// <summary>One walk of the test projects: files scanned, files carrying the marker, unmarked real-timer files.</summary>
    private sealed class TimerScan
    {
        public required int Scanned { get; init; }

        public required int Marked { get; init; }

        public required IReadOnlyList<string> Unmarked { get; init; }
    }
}
