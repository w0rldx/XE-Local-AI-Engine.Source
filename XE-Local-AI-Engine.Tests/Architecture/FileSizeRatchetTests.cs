namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A production C# file stays at or under 1000 lines; a file already over it may not grow past its ceiling.</summary>
/// <remarks>
///     The decision to split a giant, or to record why it stays whole, belongs to the commit that grows it. Ceilings in
///     <c>Architecture/FileSizeAllowlist.txt</c> are rounded up to a multiple of 50 and only go down. Test projects and
///     generated code are out of scope.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class FileSizeRatchetTests
{
    /// <summary>Set to <c>1</c> to rewrite the ceilings down to what the tree measures. Always fails afterwards.</summary>
    private const string ShrinkVariable = "XE_FILE_SIZE_SHRINK";

    private const int MaxLines = 1000;

    private const int CeilingStep = 50;

    /// <summary>Today's entry count. Only ever lowered, in the commit that shrinks the list.</summary>
    private const int MaxEntries = 24;

    /// <summary>A floor under today's production file count: a walk that lost its roots fails here.</summary>
    private const int ProductionFileFloor = 3500;

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "FileSizeAllowlist.txt");

    private static readonly Lazy<Dictionary<string, int>> Measured = new(Measure, isThreadSafe: true);

    [Test]
    public void ProductionFiles_StayUnderTheLineLimitOrTheirCeiling()
    {
        var measured = Measured.Value;
        var ceilings = Allowlist();

        AssertEx.True(measured.Count >= ProductionFileFloor,
            $"Only {measured.Count} production C# files were measured, below the floor of {ProductionFileFloor}. The "
            + "walk is reading the wrong directories, so the ratchet cannot fire.");

        if (string.Equals(Environment.GetEnvironmentVariable(ShrinkVariable), "1", StringComparison.Ordinal))
        {
            Shrink(ceilings, measured);

            AssertEx.True(false,
                $"{ShrinkVariable} rewrote Architecture/FileSizeAllowlist.txt down to what the tree measures. It never "
                + "adds a file and never raises a ceiling, and it always fails so a rewrite is never mistaken for a "
                + "green run. Review the diff, unset the variable and re-run.");
        }

        var offenders = measured
                        .Where(file => file.Value > ceilings.GetValueOrDefault(file.Key, MaxLines))
                        .Select(file => $"{file.Key}: {file.Value} lines (allowed "
                                        + $"{ceilings.GetValueOrDefault(file.Key, MaxLines)})")
                        .Order(StringComparer.Ordinal)
                        .ToList();

        AssertEx.Empty(offenders,
            $"A production C# file is over {MaxLines} lines, or over its ceiling in Architecture/FileSizeAllowlist.txt "
            + "(docs/wiki/16-code-conventions.md, \"File size\"). Split it at a real seam, or bring it back under its "
            + "ceiling; the list never gains a file and a ceiling never rises:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The shrink-only half: an entry whose file is gone or back under the limit is deleted.</summary>
    /// <remarks>A file below its ceiling but still over the limit is not stale; the shrink variable lowers it.</remarks>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var measured = Measured.Value;
        var stale = Allowlist()
                    .Where(entry => measured.GetValueOrDefault(entry.Key) <= MaxLines)
                    .Select(entry => $"{entry.Key}|{entry.Value}   -> delete the line ("
                                     + (measured.TryGetValue(entry.Key, out var lines)
                                         ? $"{lines} lines now)"
                                         : "the file is no longer measured)"))
                    .Order(StringComparer.Ordinal)
                    .ToList();

        AssertEx.Empty(stale,
            "Architecture/FileSizeAllowlist.txt lists a file that is gone or back under the limit. The list is "
            + $"shrink-only: delete the lines below, or re-run with {ShrinkVariable}=1 to rewrite the whole file:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>The ratchet: the list never grows back.</summary>
    [Test]
    public void TheAllowlist_DoesNotGrow()
    {
        var entries = Allowlist().Count;

        AssertEx.True(entries <= MaxEntries,
            $"Architecture/FileSizeAllowlist.txt holds {entries} entries, above MaxEntries ({MaxEntries}): shrink the "
            + "allowlist, then lower MaxEntries.");
    }

    [Test]
    [Arguments(1001, 1050)]
    [Arguments(1050, 1050)]
    [Arguments(1051, 1100)]
    [Arguments(1452, 1500)]
    public void Ceilings_RoundUpToAMultipleOfFifty(int lines, int expected)
    {
        AssertEx.Equal(expected, RoundUp(lines));
    }

    private static int RoundUp(int lines) =>
        (lines + CeilingStep - 1) / CeilingStep * CeilingStep;

    private static Dictionary<string, int> Measure()
    {
        var measured = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            if (EnforcedSourceFiles.IsProduction(relative))
            {
                measured[relative] = File.ReadLines(path).Count();
            }
        }

        return measured;
    }

    /// <summary>Reads <c>file|ceiling</c>; a ceiling is a multiple of 50 above the limit.</summary>
    private static Dictionary<string, int> Allowlist()
    {
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(AllowlistPath).Select(raw => raw.Trim())
                                 .Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            var fields = line.Split('|');

            if (fields.Length != 2
                || !int.TryParse(fields[1], out var ceiling)
                || ceiling <= MaxLines
                || ceiling % CeilingStep != 0
                || !entries.TryAdd(fields[0], ceiling))
            {
                throw new InvalidDataException("Architecture/FileSizeAllowlist.txt holds a line that is not a unique "
                                               + $"'file|ceiling' entry with a ceiling above {MaxLines} in steps of "
                                               + $"{CeilingStep}: {line}");
            }
        }

        return entries;
    }

    /// <summary>Rewrites each kept entry as <c>min(ceiling, measured rounded up)</c>, dropping gone or small files.</summary>
    private static void Shrink(Dictionary<string, int> ceilings, Dictionary<string, int> measured)
    {
        var text = new StringBuilder(AllowlistHeader);

        foreach (var (file, ceiling) in ceilings.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var lines = measured.GetValueOrDefault(file);

            if (lines > MaxLines)
            {
                text.Append(file).Append('|').Append(Math.Min(ceiling, RoundUp(lines))).Append('\n');
            }
        }

        File.WriteAllText(AllowlistPath, text.ToString());
    }

    private const string AllowlistHeader = """
                                           # Line ceilings for production C# files over 1000 lines — the allowlist for FileSizeRatchetTests.
                                           #
                                           # Format: <repository-relative file>|<ceiling>, one line per file, sorted. A production file with no line
                                           # here stays at or under 1000 lines. A listed file stays at or under its ceiling, which was its size when
                                           # it was listed rounded up to a multiple of 50 (for a file another branch also grows, the larger of the
                                           # two sizes).
                                           #
                                           # The list is SHRINK-ONLY. A file that is gone or back at 1000 lines or fewer fails as stale until its line
                                           # is deleted. To lower every ceiling to what the tree measures, run the test with XE_FILE_SIZE_SHRINK=1: it
                                           # never adds a file, never raises a ceiling, and always fails afterwards.

                                           """;
}
