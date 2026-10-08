namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Production code takes the registered <see cref="TimeProvider" />; it never falls back to the system clock.</summary>
/// <remarks>
///     A <c>?? TimeProvider.System</c> default or a nullable <c>GetService&lt;TimeProvider&gt;()</c> lets a
///     composition that forgot the clock run on wall time, so a test's FakeTimeProvider silently stops reaching the
///     code. Resolving it with <c>GetRequiredService</c> in a factory is the registered clock and stays allowed.
///     Test projects are out of scope. Exemptions: <c>Architecture/TimeProviderDefaultAllowlist.txt</c>, shrink-only.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class TimeProviderDefaultConventionTests
{
    /// <summary>A non-vacuity floor under today's production file count.</summary>
    private const int ScannedFileFloor = 2000;

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "TimeProviderDefaultAllowlist.txt");

    private static readonly Lazy<ClockScan> Scan = new(ScanTree, isThreadSafe: true);

    [Test]
    public void ProductionCode_NeverDefaultsTheClock()
    {
        var scan = Scan.Value;

        AssertEx.True(scan.Scanned >= ScannedFileFloor,
            $"Scanned {scan.Scanned} production files, below the non-vacuity floor of {ScannedFileFloor}.");

        var allowed = Allowlist().Keys.ToHashSet(StringComparer.Ordinal);
        var unlisted = scan.Offenders.Where(file => !allowed.Contains(file)).ToList();

        AssertEx.Empty(unlisted,
            "Production code must take the registered TimeProvider through its constructor (or the options it already "
            + "receives) instead of '?? TimeProvider.System' or GetService<TimeProvider>(). Make the parameter "
            + "required and pass the injected clock from the caller. Files:"
            + Environment.NewLine + string.Join(Environment.NewLine, unlisted));
    }

    /// <summary>The shrink-only half: an entry whose file no longer defaults the clock, or is gone.</summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var offenders = Scan.Value.Offenders.ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist().Keys.Where(entry => !offenders.Contains(entry)).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(stale,
            "Architecture/TimeProviderDefaultAllowlist.txt lists a file that no longer defaults the clock or no longer "
            + "exists. The list is shrink-only: delete the line(s) below."
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    public void TheAllowlist_GivesAReasonForEveryEntry()
    {
        var unreasoned = Allowlist().Where(entry => entry.Value.Length == 0).Select(entry => entry.Key).ToList();

        AssertEx.Empty(unreasoned,
            "Every entry is '<repository-relative file>|<reason>'. Unreasoned:"
            + Environment.NewLine + string.Join(Environment.NewLine, unreasoned));
    }

    [Test]
    [Arguments("_clock = timeProvider ?? TimeProvider.System;", true)]
    [Arguments("var now = (clock ?? System.TimeProvider.System).GetUtcNow();", true)]
    [Arguments("var clock = sp.GetService<TimeProvider>();", true)]
    [Arguments("new Foo(sp.GetRequiredService<TimeProvider>())", false)]
    [Arguments("services.TryAddSingleton(TimeProvider.System);", false)]
    [Arguments("// _clock = timeProvider ?? TimeProvider.System;", false)]
    [Arguments("var text = \"?? TimeProvider.System\";", false)]
    public void TheScan_CountsOnlyAClockFallback(string source, bool counts)
    {
        AssertEx.Equal(counts, DefaultsTheClock(source));
    }

    private static bool DefaultsTheClock(string source) =>
        ClockFallback().IsMatch(SourceCommentStripper.StripCommentsAndLiterals(source));

    private static ClockScan ScanTree()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            if (relative[..relative.IndexOf('/', StringComparison.Ordinal)].Contains(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;

            if (DefaultsTheClock(File.ReadAllText(path)))
            {
                offenders.Add(relative);
            }
        }

        return new ClockScan
        {
            Scanned = scanned,
            Offenders = [.. offenders.Order(StringComparer.Ordinal)]
        };
    }

    /// <summary>Entries keyed by repository-relative file, read from the repository rather than the output directory.</summary>
    private static Dictionary<string, string> Allowlist()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(AllowlistPath).Select(raw => raw.Trim()))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('|', 2);
            entries[fields[0].Trim()] = fields.Length > 1 ? fields[1].Trim() : string.Empty;
        }

        return entries;
    }

    [GeneratedRegex(@"\?\?\s*(?:System\.)?TimeProvider\.System\b|\bGetService<\s*(?:System\.)?TimeProvider\s*>\s*\(")]
    private static partial Regex ClockFallback();

    /// <summary>One walk of the production projects: files scanned and the files that default the clock.</summary>
    private sealed class ClockScan
    {
        public required int Scanned { get; init; }

        public required IReadOnlyList<string> Offenders { get; init; }
    }
}
