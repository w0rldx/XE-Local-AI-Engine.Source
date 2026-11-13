namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Refuses <c>x ?? throw new ArgumentNullException(...)</c>: a guarded dependency is guarded with
///     <c>ArgumentNullException.ThrowIfNull(x)</c>, then assigned.
/// </summary>
/// <remarks>
///     Reads source over the shared <see cref="EnforcedSourceFiles" /> walk, like
///     <see cref="PrimaryConstructorConventionTests" />. Comments and literal content are blanked first, so prose and
///     quoted C# never count; the match tolerates a line break between <c>??</c> and <c>throw</c>. There is no
///     exemption list. Rationale: <c>docs/wiki/16-code-conventions.md</c>, "DI + class house style".
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class ArgumentNullGuardConventionTests
{
    /// <summary>A non-vacuity floor under today's count, the same walk and floor as the primary-constructor fence.</summary>
    private const int EnforcedFileFloor = 4200;

    private const string Fix = "use ArgumentNullException.ThrowIfNull(x) then assign; wiki 16 'DI + class house style'";

    [Test]
    public void EnforcedProjects_UseThrowIfNull_NeverCoalesceThrowArgumentNull()
    {
        var offenders = new List<string>();
        var files = 0;

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            files++;
            offenders.AddRange(Offences(File.ReadAllText(path)).Select(line => $"{relative}: {line}"));
        }

        AssertEx.True(files >= EnforcedFileFloor,
            $"Only {files} C# files were scanned in the enforced projects, below the floor of {EnforcedFileFloor}. "
            + "The walk is reading the wrong directories, so this fence cannot fire.");

        AssertEx.Empty(offenders,
            $"A null guard is written as '?? throw new ArgumentNullException': {Fix}. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The guard's own check: a scan that misses a split guard, or reads prose as code, cannot be trusted.</summary>
    [Test]
    public void TheGuard_FindsCoalesceThrowAndIgnoresLookalikes()
    {
        AssertEx.Equal("_a = a ?? throw new ArgumentNullException(nameof(a));",
            Offences("_a = a ?? throw new ArgumentNullException(nameof(a));").Single(),
            "A one-line '?? throw new ArgumentNullException' was not reported.");
        AssertEx.Equal("?? throw new System.ArgumentNullException(nameof(b));",
            Offences("_b = b\n    ?? throw new System.ArgumentNullException(nameof(b));").Single(),
            "A qualified guard split over two lines was not reported.");

        AssertEx.Empty(Offences("ArgumentNullException.ThrowIfNull(c);\n_c = c;").ToList(), "ThrowIfNull was reported.");
        AssertEx.Empty(Offences("// _d = d ?? throw new ArgumentNullException(nameof(d));").ToList(),
            "A comment was read as code.");
        AssertEx.Empty(Offences("const string S = \"x ?? throw new ArgumentNullException(nameof(x))\";").ToList(),
            "A string literal was read as code.");
        AssertEx.Empty(Offences("_e = e ?? throw new InvalidOperationException(\"e\");").ToList(),
            "A different exception type was reported.");
    }

    /// <summary>The trimmed source line of every coalesce-throw guard in <paramref name="source" />.</summary>
    private static IEnumerable<string> Offences(string source)
    {
        var stripped = SourceCommentStripper.StripCommentsAndLiterals(source);
        var lines = source.Split('\n');

        // Blanking keeps line breaks, so a match's line in the stripped text is the same line in the source.
        return CoalesceThrowArgumentNull().Matches(stripped)
                                          .Select(match => lines[stripped.AsSpan(0, match.Index).Count('\n')].Trim());
    }

    [GeneratedRegex(@"\?\?\s*throw\s+new\s+(?:System\.)?ArgumentNullException\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CoalesceThrowArgumentNull();
}
