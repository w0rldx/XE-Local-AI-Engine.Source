namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Refuses a positional <c>record</c> of class kind unless the allowlist gives a reason: the shapes the project
///     ships are a <c>sealed class</c> with <c>required</c>/<c>init</c> members, or a non-positional record where
///     value semantics are wanted.
/// </summary>
/// <remarks>
///     A positional record leaves nothing in IL that separates it from a non-positional one — both compile to
///     properties plus a constructor — so this reads source, like <see cref="PrimaryConstructorConventionTests" />,
///     over the same <see cref="EnforcedSourceFiles" /> walk, which that guard also asserts covers every solution
///     project. Reading files rather than assemblies has a second effect that matters here: a source file linked into
///     two test projects is seen once, where a reflection walk would report its types twice.
///     <para>
///         <c>record struct</c> is out of scope by decision: a value type is what the positional form is for. The
///         allowlist is shrink-only — a stale entry fails as loudly as an unreasoned declaration, so a conversion
///         deletes its line in the same commit. Rule and hazards: <c>docs/wiki/16-code-conventions.md</c>.
///     </para>
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class PositionalRecordConventionTests
{
    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "PositionalRecordAllowlist.txt");

    /// <summary>
    ///     A non-vacuity floor under today's count, the way <see cref="PrimaryConstructorConventionTests" /> sets its
    ///     own: a renamed directory reads too few files and fails here rather than passing on an empty scan.
    /// </summary>
    private const int EnforcedFileFloor = 4200;

    /// <summary>The scan is pure and both rules need all of it, so one walk serves them.</summary>
    private static readonly Lazy<(int Files, IReadOnlyList<(string Key, int Line)> Declarations)> Scanned =
        new(Walk, isThreadSafe: true);

    /// <summary>Declarations the scan must find. A miss here is how this fence stops seeing code.</summary>
    private static readonly (string Case, string Source, string[] Types)[] MustBeFound =
    [
        ("a bare positional record", "internal sealed record A(int X);", ["A"]),
        ("an explicit record class", "internal sealed record class B(int X);", ["B"]),
        ("a generic record with a constraint", "internal record C<T>(T Value) where T : class;", ["C"]),
        ("a nested generic type argument", "internal record D(Dictionary<string, List<int>> Map);", ["D"]),
        ("a declaration split over lines", "public sealed record E\n    <T>\n    (\n    int X);", ["E"]),
        ("a positional record with a base list", "internal sealed record F(int X) : G(X);", ["F"]),
        ("a positional record with a body", "internal sealed record H(int X)\n{\n    public int Y => X;\n}", ["H"]),
        ("a partial record", "internal sealed partial record I(int X);", ["I"]),
        ("an attributed declaration", "[Obsolete]\ninternal sealed record J(int X);", ["J"]),
        ("a nested record inside a class", "internal sealed class Outer\n{\n    private sealed record K(int X);\n}", ["K"]),
        ("a file-scoped record", "file sealed record N(int X);", ["N"]),
        ("two on one line", "record L(int X); record M(int Y);", ["L", "M"])
    ];

    /// <summary>Text that only looks like one. A guard that fires on these is one somebody deletes.</summary>
    private static readonly (string Case, string Source)[] MustNotBeFound =
    [
        ("a non-positional record", "internal sealed record O\n{\n    public int X { get; init; }\n}"),
        ("a non-positional record with a base list", "internal sealed record P : Q;"),
        ("a non-positional generic record with a generic base list", "internal sealed record R<T> : S<T>;"),
        ("a positional record struct", "internal readonly record struct T(int X, int Y);"),
        ("a record struct", "internal record struct U(int X);"),
        ("a primary-constructor class", "internal sealed class V(int x) { }"),
        ("an identifier that ends in the keyword", "var subrecord = Resolve();"),
        ("an identifier that starts with the keyword", "records.Add(value);"),
        ("a local named after the keyword", "var record = Resolve();"),
        ("a member named after the keyword", "builder.record(value);"),
        ("a verbatim identifier", "@record W(int x);"),
        ("a doc comment quoting one", "/// <summary>See <c>record X(int y)</c>.</summary>"),
        ("a line comment quoting one", "// internal sealed record Y(int z);"),
        ("a raw string literal holding C#", "const string Snippet = \"\"\"\nrecord Z(int a);\n\"\"\";"),
        ("a verbatim string holding Python", "const string Script = @\"record Foo(bar):\";"),
        ("an interpolated string holding C#", "var s = $\"record AA({Value});\";")
    ];

    [Test]
    public void EnforcedProjects_DeclareNoUnreasonedPositionalRecordClass()
    {
        var scan = Scanned.Value;

        AssertEx.True(scan.Files >= EnforcedFileFloor,
            $"Only {scan.Files} C# files were scanned in the enforced projects, below the floor of "
            + $"{EnforcedFileFloor}. The walk is reading the wrong directories, so this fence cannot fire.");

        var remaining = Allowlist();
        var offenders = new List<string>();

        foreach (var (key, line) in scan.Declarations)
        {
            if (remaining.TryGetValue(key, out var left) && left > 0)
            {
                remaining[key] = left - 1;
                continue;
            }

            offenders.Add($"{key.Replace('|', ' ')} (declared at line {line})");
        }

        AssertEx.Empty(offenders,
            "A record of class kind must not take a positional parameter list: the shape this project ships is a "
            + "sealed class with required/init members, or a non-positional record where value semantics are "
            + "wanted (docs/wiki/16-code-conventions.md). Convert the declaration(s) below, or — if one of the "
            + "reasons grouped in Architecture/PositionalRecordAllowlist.txt applies — add it there with that "
            + "reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     The shrink-only half. Without it the list keeps every reason ever written down, and the next reader cannot
    ///     tell a live exemption from one whose type was converted three slices ago.
    /// </summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var declared = Scanned.Value.Declarations
                              .GroupBy(declaration => declaration.Key, StringComparer.Ordinal)
                              .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var stale = Allowlist()
                    .Select(entry => (entry.Key, Extra: entry.Value - (declared.GetValueOrDefault(entry.Key))))
                    .Where(entry => entry.Extra > 0)
                    .Select(entry => entry.Extra == 1
                        ? entry.Key.Replace('|', ' ')
                        : $"{entry.Key.Replace('|', ' ')} (listed {entry.Extra} times too often)")
                    .Order(StringComparer.Ordinal)
                    .ToList();

        AssertEx.Empty(stale,
            "Architecture/PositionalRecordAllowlist.txt reasons away a positional record class that is no longer "
            + "declared there. The list is shrink-only: delete the line(s) below in the commit that converted or "
            + "moved the type."
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    ///     The guard's own check. A scan that stopped recognising a split declaration, or that read a quoted snippet
    ///     as code, reads clean source everywhere and reports a fence that cannot fire.
    /// </summary>
    [Test]
    public void TheGuard_FindsRealDeclarationsAndIgnoresLookalikes()
    {
        foreach (var (name, source, types) in MustBeFound)
        {
            AssertEx.Equal(string.Join(", ", types),
                string.Join(", ", Declarations(source).Select(site => site.Type)),
                $"The '{name}' case did not report the expected declaration(s). A miss here is how this fence stops "
                + "seeing code.");
        }

        foreach (var (name, source) in MustNotBeFound)
        {
            AssertEx.Empty(Declarations(source).Select(site => site.Type).ToList(),
                $"The '{name}' case was read as a positional record class. A guard that fires on a record struct, a "
                + "non-positional record or quoted text is one somebody deletes.");
        }

        var (line, type) = Declarations("var a = 1;\nvar b = 2;\ninternal sealed record Late(int X);").Single();
        AssertEx.Equal(3, line, $"The declaration of '{type}' was reported on the wrong line.");
    }

    // ---------------------------------------------------------------- the allowlist

    /// <summary>
    ///     The reasoned exemptions as a multiset of <c>file|Name</c>: one entry per DECLARATION, because two
    ///     declarations can share a file and a simple name (one pair does), and a set would let a conversion of one
    ///     of them hide behind the other.
    /// </summary>
    /// <remarks>
    ///     Read from the repository, not from the test output directory, exactly as the source scan is: no
    ///     <c>CopyToOutputDirectory</c> to keep in step, and one failure mode for both halves instead of two.
    /// </remarks>
    private static Dictionary<string, int> Allowlist()
    {
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in File.ReadLines(AllowlistPath))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            entries[line] = entries.GetValueOrDefault(line) + 1;
        }

        return entries;
    }

    // ---------------------------------------------------------------- scanning

    private static (int Files, IReadOnlyList<(string Key, int Line)> Declarations) Walk()
    {
        var declarations = new List<(string Key, int Line)>();
        var files = 0;

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            files++;

            declarations.AddRange(Declarations(File.ReadAllText(path))
                .Select(site => ($"{relative}|{site.Type}", site.Line)));
        }

        return (files, declarations);
    }

    /// <summary>
    ///     Every <c>record</c> declaration of class kind in <paramref name="source" /> that carries a parameter list.
    /// </summary>
    /// <remarks>
    ///     Anchoring on the keyword is what keeps a method or a local named <c>record</c> out: neither is followed by
    ///     an identifier. An optional <c>class</c> is stepped over, a <c>struct</c> after the keyword ends the match,
    ///     and the parameter list must open before any base list or constraint, so a non-positional
    ///     <c>record P : Q</c> is not one. Comments and literal content are blanked first, so quoted C# is not a
    ///     declaration.
    /// </remarks>
    private static IEnumerable<(int Line, string Type)> Declarations(string source)
    {
        const string Keyword = "record";
        var text = SourceCommentStripper.StripCommentsAndLiterals(source);

        for (var index = text.IndexOf(Keyword, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(Keyword, index + 1, StringComparison.Ordinal))
        {
            if (!IsWholeWord(text, index, Keyword.Length) || (index > 0 && text[index - 1] == '@'))
            {
                continue;
            }

            var name = ReadName(text, index + Keyword.Length, out var after);

            if (name is "struct")
            {
                continue;
            }

            if (name is "class")
            {
                name = ReadName(text, after, out after);
            }

            if (name is null)
            {
                continue;
            }

            var head = SkipTypeParameters(text, after);

            if (head is null || !OpensParameterList(text, head.Value))
            {
                continue;
            }

            yield return (LineOf(text, index), name);
        }
    }

    /// <summary>Reads the identifier after <paramref name="from" />, leaving <paramref name="after" /> past it.</summary>
    private static string? ReadName(string text, int from, out int after)
    {
        after = from;

        while (after < text.Length && char.IsWhiteSpace(text[after]))
        {
            after++;
        }

        var start = after;

        while (after < text.Length && IsWordCharacter(text[after]))
        {
            after++;
        }

        return after == start ? null : text[start..after];
    }

    /// <summary>
    ///     Steps over a type-parameter list, counting angle brackets so a nested one stays whole. Returns null when
    ///     the list is never closed.
    /// </summary>
    private static int? SkipTypeParameters(string text, int from)
    {
        var index = from;

        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        if (index >= text.Length || text[index] != '<')
        {
            return index;
        }

        var depth = 0;

        for (; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;

                    if (depth == 0)
                    {
                        return index + 1;
                    }

                    break;
                default:
                    break;
            }
        }

        return null;
    }

    /// <summary>A positional parameter list opens immediately after the name, ahead of any base list or constraint.</summary>
    private static bool OpensParameterList(string text, int from)
    {
        var index = from;

        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length && text[index] == '(';
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;

        for (var scan = 0; scan < index; scan++)
        {
            if (text[scan] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static bool IsWholeWord(string text, int start, int length) =>
        (start == 0 || !IsWordCharacter(text[start - 1]))
        && (start + length >= text.Length || !IsWordCharacter(text[start + length]));

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';
}
