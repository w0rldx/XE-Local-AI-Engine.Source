namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Refuses a primary constructor on a class or struct: the project declares conventional constructors instead.
/// </summary>
/// <remarks>
///     A primary constructor leaves no trace in IL, so this reads source, like <see cref="ConfigureAwaitPolicyTests" />,
///     over the shared <see cref="EnforcedSourceFiles" /> walk. Every project in the solution is fenced, with no
///     exemption; records are out of scope. Rationale: <c>docs/wiki/16-code-conventions.md</c>.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class PrimaryConstructorConventionTests
{
    /// <summary>The two declaration keywords the rule covers; <c>record</c> and <c>interface</c> are out of scope.</summary>
    private static readonly string[] Keywords = ["class", "struct"];

    /// <summary>
    ///     A non-vacuity floor under today's count, the way <see cref="ConfigureAwaitPolicyTests" /> sets its own: a
    ///     renamed directory reads too few files and fails here rather than passing on an empty scan.
    /// </summary>
    private const int EnforcedFileFloor = 4200;

    /// <summary>Declarations the scan must find. A miss here is how this fence stops seeing code.</summary>
    private static readonly (string Case, string Source, string[] Types)[] MustBeFound =
    [
        ("a plain class", "internal sealed class A(int x) : IB { }", ["A"]),
        ("a struct", "private readonly struct B(int x) { }", ["B"]),
        ("a ref struct", "private ref struct C(ReadOnlySpan<byte> s) { }", ["C"]),
        ("a generic class with a constraint", "internal class D<T>(T t) where T : class { }", ["D"]),
        ("a nested generic type argument", "internal class E<T>(Dictionary<string, List<T>> m) { }", ["E"]),
        ("a declaration split over lines", "internal sealed class F\n    <T>\n    (\n    int x) { }", ["F"]),
        ("a file-scoped type", "file sealed class G(int x) { }", ["G"]),
        ("a partial class", "internal sealed partial class H(int x) { }", ["H"]),
        ("a body-less declaration", "public sealed class I(string m) : Exception(m);", ["I"]),
        ("an attributed declaration", "[Obsolete]\ninternal sealed class J(int x) { }", ["J"]),
        ("a nested type inside a clean one",
            "internal sealed class Outer\n{\n    private sealed class K(int x) { }\n}", ["K"]),
        ("two on one line", "class L(int x) { } class M(int y) { }", ["L", "M"])
    ];

    /// <summary>Text that only looks like one. A guard that fires on these is one somebody deletes.</summary>
    private static readonly (string Case, string Source)[] MustNotBeFound =
    [
        ("a conventional constructor whose name matches a type in the file",
            "internal class N\n{\n    protected N(string message) { }\n}"),
        ("a positional record", "internal sealed record O(string Name, int Count);"),
        ("a positional record class", "internal sealed record class P(string Name);"),
        ("a positional record struct", "internal readonly record struct Q(int X, int Y);"),
        ("a class constraint", "internal sealed class R<T> where T : class { }"),
        ("a class constraint followed by more", "internal sealed class S<T> where T : class, IDisposable { }"),
        ("a plain declaration with a base list", "internal sealed class T : U { }"),
        ("a generic declaration with a generic base list", "internal sealed class V<W> : X<W> { }"),
        ("an identifier that ends in the keyword", "var subclass = Resolve();"),
        ("a doc comment quoting one", "/// <summary>See <c>class Y(int z)</c>.</summary>"),
        ("a line comment quoting one", "// internal sealed class Z(int a) { }"),
        ("a raw string literal holding C#", "const string Snippet = \"\"\"\nclass AA(int b) { }\n\"\"\";"),
        ("a verbatim string holding Python", "const string Script = @\"class BB(unittest.TestCase):\";"),
        ("an interpolated string holding C#", "var s = $\"class CC({Value}) {{ }}\";")
    ];

    [Test]
    public void EnforcedProjects_DeclareNoPrimaryConstructors()
    {
        var offenders = new List<string>();
        var files = 0;

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            files++;

            offenders.AddRange(Declarations(File.ReadAllText(path))
                .Select(site => $"{relative}:{site.Line}  {site.Keyword} {site.Type}"));
        }

        AssertEx.True(files >= EnforcedFileFloor,
            $"Only {files} C# files were scanned in the enforced projects, below the floor of {EnforcedFileFloor}. "
            + "The walk is reading the wrong directories, so this fence cannot fire.");

        AssertEx.Empty(offenders,
            "A class or struct declares conventional constructors, never a primary constructor: the guard, the "
            + "field and the injected name belong in one readable block, and a constructor body stays "
            + "available. Convert the declaration(s) below:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    ///     The fence covers the solution, not a hand-kept list: a project whose directory the walk cannot find
    ///     contributes no files and would be exempt without anyone deciding that.
    /// </summary>
    [Test]
    public void EverySolutionProject_IsScanned()
    {
        var projects = EnforcedSourceFiles.SolutionProjects();

        AssertEx.NotEmpty(projects, "No projects were read from the solution file.");

        var absent = projects.Where(project => !Directory.Exists(RepositoryPaths.Combine(project))).ToList();
        AssertEx.Empty(absent,
            "A solution project's directory was not found under the repository root, so its sources were never "
            + "scanned: " + string.Join(", ", absent));
    }

    /// <summary>
    ///     The guard's own check. A scan that stopped recognising a split declaration, or that read a quoted snippet as
    ///     code, reads clean source everywhere and reports a fence that cannot fire.
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
                $"The '{name}' case was read as a primary constructor. A guard that fires on a record, a constraint "
                + "or quoted text is one somebody deletes.");
        }

        var (line, _, type) = Declarations("var a = 1;\nvar b = 2;\ninternal sealed class Late(int x) { }").Single();
        AssertEx.Equal(3, line, $"The declaration of '{type}' was reported on the wrong line.");
    }

    // ---------------------------------------------------------------- scanning

    /// <summary>
    ///     Every <c>class</c>/<c>struct</c> declaration in <paramref name="source" /> that carries a parameter list.
    /// </summary>
    /// <remarks>
    ///     Anchoring on the keyword is what keeps a conventional constructor out: <c>protected N(string m)</c> has no
    ///     keyword before it. A <c>record</c> before the keyword, or a type-parameter constraint after a <c>:</c>, is
    ///     skipped. Comments and literal content are blanked first, so quoted C# is not a declaration.
    /// </remarks>
    private static IEnumerable<(int Line, string Keyword, string Type)> Declarations(string source)
    {
        var text = SourceCommentStripper.StripCommentsAndLiterals(source);

        foreach (var keyword in Keywords)
        {
            for (var index = text.IndexOf(keyword, StringComparison.Ordinal);
                 index >= 0;
                 index = text.IndexOf(keyword, index + 1, StringComparison.Ordinal))
            {
                if (!IsWholeWord(text, index, keyword.Length)
                    || PrecedingWord(text, index) is "record"
                    || PrecedesConstraint(text, index))
                {
                    continue;
                }

                var name = ReadName(text, index + keyword.Length, out var after);

                if (name is null)
                {
                    continue;
                }

                var head = SkipTypeArguments(text, after);

                if (head is null || !OpensParameterList(text, head.Value))
                {
                    continue;
                }

                yield return (LineOf(text, index), keyword, name);
            }
        }
    }

    /// <summary>A <c>where T : class</c> constraint is the one place the keyword follows a <c>:</c> or a <c>,</c>.</summary>
    private static bool PrecedesConstraint(string text, int index)
    {
        for (var scan = index - 1; scan >= 0; scan--)
        {
            if (char.IsWhiteSpace(text[scan]))
            {
                continue;
            }

            return text[scan] is ':' or ',';
        }

        return false;
    }

    private static string? PrecedingWord(string text, int index)
    {
        var end = index;

        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        var start = end;

        while (start > 0 && IsWordCharacter(text[start - 1]))
        {
            start--;
        }

        return start == end ? null : text[start..end];
    }

    /// <summary>Reads the identifier after the keyword, leaving <paramref name="after" /> on the next character.</summary>
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
    ///     Steps over a type-parameter list, counting angle brackets so a nested one stays whole. Returns null when the
    ///     list is never closed.
    /// </summary>
    private static int? SkipTypeArguments(string text, int from)
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

    /// <summary>A parameter list opens immediately after the name, ahead of any base list or constraint.</summary>
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

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';
}
