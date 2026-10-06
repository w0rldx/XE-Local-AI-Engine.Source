namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Refuses tuples in enforced C#: tuple types, <c>Tuple</c>/<c>ValueTuple</c> names and value-producing tuple
///     expressions. Deconstruction, tuple switches and positional patterns stay allowed.
/// </summary>
/// <remarks>
///     A text scan cannot tell <c>(int A, int B)</c> the type from a parameter list, so this parses each file with
///     <see cref="CSharpSyntaxTree" />: syntax only, no compilation. The walk is the same
///     <see cref="EnforcedSourceFiles" /> one <see cref="PositionalRecordConventionTests" /> reads. The allowlist
///     holds <c>file|count</c> lines and is shrink-only in both directions, so a conversion lowers or deletes its
///     line in the same commit.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class NoTupleConventionTests
{
    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "TupleAllowlist.txt");

    /// <summary>The same non-vacuity floor as the positional-record guard, which reads the same walk.</summary>
    private const int EnforcedFileFloor = 4200;

    private const int ExcerptLength = 80;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);

    /// <summary>The scan is pure and every rule needs all of it, so one walk serves them.</summary>
    private static readonly Lazy<Scan> Scanned = new(Walk, isThreadSafe: true);

    /// <summary>Each banned form, with how many nodes it must count. A miss here is how this fence stops seeing code.</summary>
    private static readonly Case[] MustBeFound =
    [
        new("a tuple return type", "class C { (int A, string B) M() => default; }", 1),
        new("a tuple generic argument", "class C { List<(int, int)> F = []; }", 1),
        new("a tuple parameter", "class C { void M((int, int) p) { } }", 1),
        new("a tuple field", "class C { (int X, int Y) f; }", 1),
        new("a tuple cast", "class C { void M(object o) { var x = ((int, int))o; } }", 1),
        new("a tuple local type", "class C { void M() { (int, int) x = default; } }", 1),
        new("a nested tuple type", "class C { (int, (int, int)) f; }", 2),
        new("System.Tuple of T", "class C { System.Tuple<int, int> f; }", 1),
        new("ValueTuple of T", "class C { ValueTuple<int, int> f; }", 1),
        new("an open ValueTuple in typeof", "var t = typeof(ValueTuple<,>);", 1),
        new("Tuple.Create", "var t = Tuple.Create(1, 2);", 1),
        new("System.Tuple.Create", "var t = System.Tuple.Create(1, 2);", 1),
        new("a returned tuple", "class C { object M() { return (1, 2); } }", 1),
        new("an expression-bodied tuple", "class C { object M() => (1, 2); }", 1),
        new("a tuple argument", "Use((1, 2));", 1),
        new("a tuple collection element", "object[] a = [(1, 2)];", 1),
        new("a tuple in an object initializer", "var d = new Dictionary<int, object> { [1] = (1, 2) };", 1),
        new("a LINQ projection", "var q = xs.Select(x => (x, x));", 1),
        new("a tuple dictionary key", "var d = new Dictionary<(int, int), int> { [(1, 2)] = 3 };", 2),
        new("a tuple local", "var x = (a, b);", 1),
        new("a nested tuple local", "var x = (a, (b, c));", 2),
        new("a tuple compared by a pattern", "var y = (a, b) is (1, 2);", 1)
    ];

    /// <summary>Forms the rule allows. A guard that fires on deconstruction or a parameter list is one somebody deletes.</summary>
    private static readonly Case[] MustNotBeFound =
    [
        new("a tuple switch expression", "var r = (a, b) switch { (1, _) => 1, _ => 0 };", 0),
        new("a tuple switch statement", "switch (a, b) { case (1, 2): break; }", 0),
        new("a parenthesized tuple switch statement", "switch ((a, b)) { default: break; }", 0),
        new("a deconstruction assignment", "(x, y) = Get();", 0),
        new("a declaring deconstruction assignment", "(int p, int q) = Get();", 0),
        new("a swap", "(x, y) = (y, x);", 0),
        new("a nested multi-assign", "((x, y), z) = ((1, 2), 3);", 0),
        new("a var deconstruction", "var (a, b) = Get();", 0),
        new("a foreach deconstruction", "foreach (var (k, v) in dict) { }", 0),
        new("a foreach tuple deconstruction", "foreach ((var k, var v) in dict) { }", 0),
        new("a positional pattern", "if (o is (1, _)) { }", 0),
        new("a parameter list", "class C { void M(int a, int b) { } }", 0),
        new("a parenthesized expression", "var x = (a + b) * c;", 0),
        new("a member named Tuple", "var t = holder.Tuple;", 0),
        new("quoted text", "var s = \"(int, int) Tuple.Create(1, 2)\"; // (int, int)", 0)
    ];

    [Test]
    public void EnforcedProjects_DeclareNoTupleBeyondTheAllowlist()
    {
        var scan = Scanned.Value;

        AssertEx.True(scan.Files >= EnforcedFileFloor,
            $"Only {scan.Files} C# files were scanned in the enforced projects, below the floor of "
            + $"{EnforcedFileFloor}. The walk is reading the wrong directories, so this fence cannot fire.");

        var allowed = Allowlist();
        var over = scan.Hits
                       .Where(file => IsProduction(file.Key) || file.Value.Count > allowed.GetValueOrDefault(file.Key))
                       .OrderBy(file => file.Key, StringComparer.Ordinal)
                       .ToList();

        var message = new StringBuilder(
            "Tuples are not used in this project: give the shape a named type (docs/wiki/16-code-conventions.md). "
            + "Deconstruction, tuple switches and positional patterns stay allowed. Convert the node(s) below. "
            + "Production code holds none; the allowlist only grandfathers test projects. "
            + "The file|count lines for Architecture/TupleAllowlist.txt are:");

        foreach (var (file, hits) in over)
        {
            message.AppendLine().Append(file).Append('|').Append(hits.Count);
        }

        message.AppendLine().AppendLine().Append("Nodes (allowlisted count in brackets):");

        foreach (var (file, hits) in over)
        {
            message.AppendLine().Append(file).Append(" [").Append(allowed.GetValueOrDefault(file)).Append(']');

            foreach (var hit in hits)
            {
                message.AppendLine().Append("  line ").Append(hit.Line).Append(' ').Append(hit.Kind)
                       .Append(": ").Append(hit.Excerpt);
            }
        }

        AssertEx.Empty(over.Select(file => file.Key).ToList(), message.ToString());
    }

    /// <summary>
    ///     The shrink-only half: a count above today's, or a file that no longer exists or has none, fails, so room
    ///     freed by a conversion cannot be spent twice.
    /// </summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var hits = Scanned.Value.Hits;

        var stale = Allowlist()
                    .Where(entry => entry.Value > (hits.TryGetValue(entry.Key, out var found) ? found.Count : 0))
                    .Select(entry => hits.TryGetValue(entry.Key, out var found)
                        ? $"{entry.Key}|{found.Count} (listed as {entry.Value})"
                        : $"{entry.Key} (listed as {entry.Value}, none found: delete the line)")
                    .Order(StringComparer.Ordinal)
                    .ToList();

        AssertEx.Empty(stale,
            "Architecture/TupleAllowlist.txt allows more tuples than the file(s) below hold. The list is "
            + "shrink-only: lower the count to the one shown, or delete the line, in the commit that converted them."
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    ///     The guard's own check. A walk that stopped seeing a banned form, or that counted deconstruction, reads
    ///     clean source everywhere and reports a fence that cannot fire.
    /// </summary>
    [Test]
    public void TheGuard_CountsBannedFormsAndIgnoresAllowedOnes()
    {
        foreach (var @case in MustBeFound.Concat(MustNotBeFound))
        {
            var found = Banned(@case.Source);

            AssertEx.Equal(@case.Expected, found.Count,
                $"The '{@case.Name}' case counted {found.Count} banned node(s), expected {@case.Expected}: "
                + string.Join("; ", found.Select(hit => $"{hit.Kind} {hit.Excerpt}")));
        }

        var late = Banned("var a = 1;\nvar b = 2;\nvar c = (a, b);").Single();
        AssertEx.Equal(3, late.Line, $"The {late.Kind} '{late.Excerpt}' was reported on the wrong line.");
        AssertEx.Equal(nameof(SyntaxKind.TupleExpression), late.Kind, "The banned node reported the wrong kind.");
    }

    // ---------------------------------------------------------------- the allowlist

    /// <summary>Production holds no tuples; only test projects and the shared test doubles are grandfathered.</summary>
    private static bool IsProduction(string relative) =>
        !relative.Contains(".Tests/", StringComparison.Ordinal)
        && !relative.Contains(".Tests.", StringComparison.Ordinal)
        && !relative.Contains(".Testing/", StringComparison.Ordinal)
        && !relative.Contains(".Testing.", StringComparison.Ordinal);

    /// <summary>The <c>file|count</c> lines, read from the repository like the source scan, not the output directory.</summary>
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

            var separator = line.LastIndexOf('|');
            entries[line[..separator]] = int.Parse(line[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }

        return entries;
    }

    // ---------------------------------------------------------------- scanning

    private static Scan Walk()
    {
        var hits = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
        var files = 0;

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            files++;
            var found = Banned(File.ReadAllText(path));

            if (found.Count > 0)
            {
                hits[relative] = found;
            }
        }

        return new Scan { Files = files, Hits = hits };
    }

    private static List<Hit> Banned(string source) =>
        CSharpSyntaxTree.ParseText(source, ParseOptions)
                        .GetRoot()
                        .DescendantNodes()
                        .Where(IsBanned)
                        .Select(node => new Hit(
                            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            node.Kind().ToString(),
                            ExcerptOf(node)))
                        .ToList();

    private static bool IsBanned(SyntaxNode node) => node switch
    {
        TupleTypeSyntax => true,
        GenericNameSyntax generic => IsTupleName(generic.Identifier),
        IdentifierNameSyntax name when IsTupleName(name.Identifier) => name.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Expression == name => true,
            MemberAccessExpressionSyntax access => access.Parent is MemberAccessExpressionSyntax outer
                                                   && outer.Expression == access,
            QualifiedNameSyntax qualified => qualified.Right == name,
            _ => false
        },
        TupleExpressionSyntax tuple => !IsAllowedTupleExpression(tuple),
        _ => false
    };

    private static bool IsTupleName(SyntaxToken identifier) => identifier.ValueText is "Tuple" or "ValueTuple";

    /// <summary>A tuple nested in another is judged by the outermost one, so a whole deconstruction target stays allowed.</summary>
    private static bool IsAllowedTupleExpression(TupleExpressionSyntax tuple)
    {
        ExpressionSyntax outer = tuple;

        while (outer.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax parent })
        {
            outer = parent;
        }

        return outer.Parent switch
        {
            SwitchExpressionSyntax switchExpression => switchExpression.GoverningExpression == outer,
            SwitchStatementSyntax switchStatement => switchStatement.Expression == outer,
            AssignmentExpressionSyntax assignment => assignment.Left == outer
                                                     || assignment.Left is TupleExpressionSyntax,
            ForEachVariableStatementSyntax forEach => forEach.Variable == outer,
            _ => false
        };
    }

    private static string ExcerptOf(SyntaxNode node)
    {
        var text = string.Join(' ', node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > ExcerptLength ? text[..ExcerptLength] + "..." : text;
    }

    private readonly record struct Case(string Name, string Source, int Expected);

    private readonly record struct Hit(int Line, string Kind, string Excerpt);

    private sealed class Scan
    {
        public required int Files { get; init; }

        public required Dictionary<string, List<Hit>> Hits { get; init; }
    }
}
