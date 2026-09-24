namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Holds every production C# file to one top-level type, per <c>docs/wiki/16-code-conventions.md</c>.
/// </summary>
/// <remarks>
///     The rule is general; the guards that already read file identity are not.
///     <see cref="EndpointConventionTests" /> owns it for endpoints, by reflection, with no allowlist;
///     <c>ServiceModelColocationTests</c> owns it for six named service folders. This one covers everything else,
///     against a shrink-only allowlist and four exception shapes that are the operator's call, not the scanner's.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class FilePlacementConventionTests
{
    /// <summary>Set to <c>1</c> to rewrite the allowlist down to what the tree declares. Always fails afterwards.</summary>
    private const string ShrinkVariable = "XE_FILE_PLACEMENT_SHRINK";

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "FilePlacementAllowlist.txt");

    /// <summary>
    ///     A floor a little under today's file count, which the cleanup batches do not lower.
    /// </summary>
    /// <remarks>A walk that lost its roots reads too few files and fails here rather than reporting a clean tree.</remarks>
    private const int EnforcedFileFloor = 4200;

    /// <summary>Family files: a plural name declares that the file holds a vocabulary rather than a type.</summary>
    private static readonly string[] FamilySuffixes = ["Dtos.cs", "Contracts.cs", "ServiceModels.cs", "Models.cs"];

    /// <summary>Everything that may stand between a declaration's start and its type keyword.</summary>
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public",
        "internal",
        "private",
        "protected",
        "file",
        "static",
        "sealed",
        "abstract",
        "partial",
        "readonly",
        "ref",
        "unsafe",
        "new",
        "extern",
        "required",
        "virtual",
        "override"
    };

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "class",
        "struct",
        "interface",
        "enum",
        "record",
        "delegate"
    };

    /// <summary>The scan is pure and every test needs all of it, so one walk serves them.</summary>
    private static readonly Lazy<Scan> Scanned = new(Walk, isThreadSafe: true);

    /// <summary>What the scanner must read, and exactly how it must report it.</summary>
    /// <remarks>
    ///     The two ways a brace-counting scan can be wrong are counting a nested type as top-level and losing a real
    ///     one behind a construct it mis-reads. Both are covered here, including the nesting case that made every
    ///     regex inventory this repository has produced over-count.
    /// </remarks>
    private static readonly (string Case, string Source, string Expected)[] Cases =
    [
        ("two types with generic base lists in one file",
            "namespace N;\npublic sealed class A : Base<R> { }\npublic sealed class B : Other<S> { }",
            "class A, class B"),
        ("a nested type", "namespace N;\npublic sealed class A { private sealed class B { } }", "class A"),
        ("a type nested two deep",
            "namespace N;\npublic static class A { private static class B { private enum C { X } } }", "class A"),
        ("a block namespace", "namespace N\n{\n    public sealed class A { }\n    public enum B { X }\n}",
            "class A, enum B"),
        ("no namespace at all", "public sealed class A { }", "class A"),
        ("an attributed declaration", "namespace N;\n[Obsolete]\npublic sealed class A { }", "class A"),
        ("an attribute carrying an array initializer",
            "namespace N;\n[Values(new[] { 1, 2 })]\npublic sealed class A { }", "class A"),
        ("a generic type with a constraint",
            "namespace N;\npublic sealed class A<T> : B<T> where T : class { }", "class A"),
        ("a primary constructor and a base list",
            "namespace N;\npublic sealed class A(int x) : B(x) { }", "class A"),
        ("a positional record", "namespace N;\npublic sealed record A(int X);", "record A"),
        ("a record struct", "namespace N;\npublic readonly record struct A(int X);", "record A"),
        ("an enum with a backing type", "namespace N;\npublic enum A : byte { X }", "enum A"),
        ("a delegate", "namespace N;\npublic delegate void A(int x);", "delegate A"),
        ("a generic delegate returning a generic type",
            "namespace N;\npublic delegate Func<int> A<T>(T value);", "delegate A"),
        ("a file-local type", "namespace N;\nfile sealed class A { }", "class A"),
        ("two partial halves in one file",
            "namespace N;\npublic sealed partial class A { }\npublic sealed partial class A { }", "class A, class A"),
        ("a type following one that has a body",
            "namespace N;\npublic sealed class A\n{\n    public void M() { if (true) { } }\n}\npublic sealed class B { }",
            "class A, class B"),
        ("usings inside the namespace",
            "namespace N;\n\nusing System;\nusing System.Text;\n\npublic sealed class A { }", "class A"),
        ("an interface beside its implementation",
            "namespace N;\npublic interface IA { }\npublic sealed class A : IA { }", "interface IA, class A"),
        ("a type quoted in a raw string",
            "namespace N;\npublic static class A\n{\n    public const string S = \"\"\"\npublic sealed class B { }\n\"\"\";\n}",
            "class A"),
        ("a type quoted in a comment", "namespace N;\n// public sealed class B { }\npublic sealed class A { }",
            "class A"),
        ("a type quoted in a doc block",
            "namespace N;\n/// <summary>See <c>public sealed class B { }</c>.</summary>\npublic sealed class A { }",
            "class A"),
        ("a local named after a keyword", "var record = Resolve();\nvar other = 1;", ""),
        ("a member access on a local named after a keyword", "record.Add(1);\nvar other = 1;", ""),
        ("an anonymous method", "Action a = delegate { };\nvar b = 2;", ""),
        ("a statement block followed by a statement",
            "foreach (var x in y)\n{\n    Use(x);\n}\n\nrecord.Done();", "")
    ];

    [Test]
    public void ProductionProjects_DeclareOneTopLevelTypePerFile()
    {
        var scan = Scanned.Value;
        var allowed = Allowlist();

        if (string.Equals(Environment.GetEnvironmentVariable(ShrinkVariable), "1", StringComparison.Ordinal))
        {
            Shrink(allowed, scan);

            AssertEx.True(false,
                $"{ShrinkVariable} rewrote Architecture/FilePlacementAllowlist.txt down to what the tree declares. "
                + "The rewrite never adds a key and never raises a count, and it always fails so that a "
                + "regeneration can never be mistaken for a green run. Review the diff, unset the variable and "
                + "re-run.");
        }

        var offenders = scan.Counted
                            .Where(entry => entry.Value > Math.Max(1, allowed.GetValueOrDefault(entry.Key)))
                            .Select(entry => $"{entry.Key}|{entry.Value} (allowed "
                                             + $"{Math.Max(1, allowed.GetValueOrDefault(entry.Key))}): "
                                             + string.Join(", ", scan.Types[entry.Key]))
                            .Order(StringComparer.Ordinal)
                            .ToList();

        AssertEx.Empty(offenders,
            "A production C# file declares more than one top-level type without one of the four exception shapes "
            + "(docs/wiki/16-code-conventions.md): a family file named *Dtos.cs, *Contracts.cs, *ServiceModels.cs "
            + "or *Models.cs; an interface beside its single implementation; one type beside only enums and "
            + "delegates; or an I*Store.cs / I*Service.cs carrying its own vocabulary. The allowlist is "
            + "SHRINK-ONLY, so the fix is to split the file, not to raise the count:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The shrink-only half: an entry may never reserve more room than the tree uses.</summary>
    /// <remarks>
    ///     Without it the list keeps every count it was seeded with, and a file split three batches ago silently
    ///     re-earns the room it gave up.
    /// </remarks>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var scan = Scanned.Value;
        var stale = new List<string>();

        foreach (var (file, count) in Allowlist())
        {
            if (!scan.Seen.Contains(file))
            {
                stale.Add($"{file}|{count}   -> delete the line, '{file}' is no longer scanned");
                continue;
            }

            var actual = scan.Counted.GetValueOrDefault(file);

            if (actual < count)
            {
                stale.Add(actual <= 1
                    ? $"{file}|{count}   -> delete the line"
                    : $"{file}|{count}   -> write {file}|{actual}");
            }
        }

        AssertEx.Empty(stale,
            "Architecture/FilePlacementAllowlist.txt reserves more room than the tree uses. The list is "
            + "shrink-only: the commit that splits a file lowers or deletes its line in the same change, so the "
            + $"room cannot be spent again. Apply the replacements below, or re-run with {ShrinkVariable}=1 to "
            + "rewrite the whole file:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale.Order(StringComparer.Ordinal)));
    }

    /// <summary>The guard's own check: every shape the scanner must read, and every lookalike it must not.</summary>
    /// <remarks>
    ///     A scanner that counts a nested type, or one that stops recognising a construct, reports an allowlist
    ///     nobody is holding. The non-vacuity floor on the walk is asserted here for the same reason.
    /// </remarks>
    [Test]
    public void TheGuard_ReadsRealDeclarationsAndIgnoresLookalikes()
    {
        foreach (var (name, source, expected) in Cases)
        {
            AssertEx.Equal(expected,
                string.Join(", ", TopLevelTypes(source).Select(declaration => declaration.ToString())),
                $"The '{name}' case was not read as expected. A miss here is how this guard stops seeing code, and "
                + "a false hit is how it becomes one somebody deletes.");
        }

        AssertEx.True(Scanned.Value.Files >= EnforcedFileFloor,
            $"Only {Scanned.Value.Files} C# files were scanned, below the floor of {EnforcedFileFloor}. The walk is "
            + "reading the wrong directories, so neither rule can fire.");
    }

    /// <summary>The pair exemption asks for a relationship, not a spelling.</summary>
    /// <remarks>
    ///     Matching <c>IFoo</c> against <c>Foo</c> by name alone exempts any two unrelated types that happen to be
    ///     spelled that way, which is an exemption nobody decided to grant.
    /// </remarks>
    [Test]
    public void TheInterfacePairExemption_RequiresTheImplementationToNameTheInterface()
    {
        (string Case, string Source, bool Exempt)[] pairs =
        [
            ("an implementation that names its interface",
                "namespace N;\npublic interface IA { }\npublic sealed class A : IA { }", true),
            ("the same pair declared implementation first",
                "namespace N;\npublic sealed class A : IA { }\npublic interface IA { }", true),
            ("an implementation that names it after another base",
                "namespace N;\npublic interface IA { }\npublic sealed class A : Base, IA { }", true),
            ("an implementation that names it as a type argument's owner",
                "namespace N;\npublic interface IA { }\npublic sealed class A : IA<int> { }", true),
            ("two unrelated types spelled like a pair",
                "namespace N;\npublic interface IA { }\npublic sealed class A { }", false),
            ("an implementation that names a different interface",
                "namespace N;\npublic interface IA { }\npublic sealed class A : IB { }", false),
            ("an implementation whose base merely starts with the name",
                "namespace N;\npublic interface IA { }\npublic sealed class A : IAsyncThing { }", false)
        ];

        foreach (var (name, source, exempt) in pairs)
        {
            var declared = TopLevelTypes(source);

            AssertEx.Equal(2, declared.Count, $"The '{name}' fixture must declare exactly two top-level types.");
            AssertEx.Equal(exempt, IsExempt("Some/Folder/A.cs", declared),
                $"The '{name}' fixture was exempted wrongly. The pair rule holds only when the implementation's "
                + "base list actually names the interface.");
        }
    }

    // ---------------------------------------------------------------- the allowlist

    /// <summary>Reads <c>file|count</c> from the repository rather than from the test output directory.</summary>
    private static Dictionary<string, int> Allowlist()
    {
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        var number = 0;

        foreach (var raw in File.ReadLines(AllowlistPath))
        {
            number++;
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('|');

            if (fields.Length != 2
                || !int.TryParse(fields[1], out var count)
                || count < 2
                || !entries.TryAdd(fields[0], count))
            {
                throw new InvalidDataException($"Architecture/FilePlacementAllowlist.txt line {number} is not a unique "
                                               + $"'file|count of at least 2' entry: {line}");
            }
        }

        return entries;
    }

    /// <summary>Rewrites the list as <c>min(allowed, declared)</c> per existing key, dropping what reached one.</summary>
    /// <remarks>It can only ever shrink: a key the file does not already hold is never added.</remarks>
    private static void Shrink(Dictionary<string, int> allowed, Scan scan)
    {
        var text = new StringBuilder(AllowlistHeader);

        var kept = allowed
                   .Where(entry => scan.Seen.Contains(entry.Key))
                   .Select(entry => (entry.Key, Count: Math.Min(entry.Value, scan.Counted.GetValueOrDefault(entry.Key))))
                   .Where(entry => entry.Count > 1)
                   .OrderBy(entry => entry.Key, StringComparer.Ordinal);

        foreach (var (file, count) in kept)
        {
            text.Append(file).Append('|').Append(count).Append('\n');
        }

        File.WriteAllText(AllowlistPath, text.ToString());
    }

    // ---------------------------------------------------------------- the walk

    /// <summary>One pass over the enforced tree: what was read, and what each offending file declares.</summary>
    private readonly record struct Scan(
        int Files,
        HashSet<string> Seen,
        Dictionary<string, int> Counted,
        Dictionary<string, IReadOnlyList<string>> Types);

    private static Scan Walk()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        var types = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var files = 0;

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            files++;

            // Every file the walk read is recorded, conforming or not: the stale half must be able to tell
            // "this file is clean now" from "this file is gone", and only a read file can be the first.
            seen.Add(relative);

            var declared = TopLevelTypes(File.ReadAllText(path));

            if (declared.Count > 1 && IsProduction(relative) && !IsExempt(relative, declared))
            {
                counted[relative] = declared.Count;
                types[relative] = [.. declared.Select(declaration => declaration.ToString())];
            }
        }

        return new Scan(files, seen, counted, types);
    }

    /// <summary>Test projects and the shared test doubles are out of scope for the one-type rule.</summary>
    private static bool IsProduction(string relative) =>
        !relative.Contains(".Tests/", StringComparison.Ordinal)
        && !relative.Contains(".Tests.", StringComparison.Ordinal)
        && !relative.Contains(".Testing/", StringComparison.Ordinal)
        && !relative.Contains(".Testing.", StringComparison.Ordinal);

    /// <summary>The four operator-approved shapes a multi-type file may take.</summary>
    private static bool IsExempt(string relative, IReadOnlyList<Declaration> declared)
    {
        var name = relative[(relative.LastIndexOf('/') + 1)..];

        if (FamilySuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return true;
        }

        // An I*Store.cs / I*Service.cs whose interface names the file: the commands, snapshots, results and
        // exceptions it takes and throws are one vocabulary, which a split would scatter over a folder.
        if (name.StartsWith('I')
            && (name.EndsWith("Store.cs", StringComparison.Ordinal)
                || name.EndsWith("Service.cs", StringComparison.Ordinal))
            && declared.Any(declaration => declaration.Kind == "interface"
                                           && string.Equals(declaration.Name, name[..^3], StringComparison.Ordinal)))
        {
            return true;
        }

        if (declared.Count == 2 && IsInterfacePair(declared[0], declared[1]))
        {
            return true;
        }

        // One primary type beside only the enums and delegates it takes or returns.
        return declared.Count(declaration => declaration.Kind is not ("enum" or "delegate")) == 1;
    }

    /// <summary>An interface beside its single implementation, in either order.</summary>
    /// <remarks>
    ///     The name match alone is a loophole: any <c>IFoo</c> beside any unrelated <c>Foo</c> would claim the
    ///     exemption without the two having anything to do with each other. The base list has to name the
    ///     interface as well, which is what makes the pair a pair.
    /// </remarks>
    private static bool IsInterfacePair(Declaration first, Declaration second)
    {
        var (contract, implementation) = first.Kind == "interface" ? (first, second) : (second, first);

        return contract.Kind == "interface"
               && implementation.Kind != "interface"
               && contract.Name.StartsWith('I')
               && string.Equals(contract.Name[1..], implementation.Name, StringComparison.Ordinal)
               && implementation.Implements(contract.Name);
    }

    // ---------------------------------------------------------------- the scanner

    /// <summary>A top-level type declaration: enough to classify the file, not to bind it.</summary>
    /// <remarks>
    ///     <c>Bases</c> is the raw text after the declaration head's base-list colon, constraints included. It is
    ///     text, not a binding, and it is read for one question only: does this type name that interface.
    /// </remarks>
    private readonly record struct Declaration(string Kind, string Name, string Bases)
    {
        public override string ToString() =>
            $"{Kind} {Name}";

        /// <summary>Whether the base list names <paramref name="type" /> as a whole word.</summary>
        internal bool Implements(string type)
        {
            for (var index = Bases.IndexOf(type, StringComparison.Ordinal);
                 index >= 0;
                 index = Bases.IndexOf(type, index + 1, StringComparison.Ordinal))
            {
                if (IsWholeWord(Bases, index, type.Length))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Every type declared at the top level of <paramref name="source" />, nested types excluded.</summary>
    /// <remarks>
    ///     Brace depth separates the two: a top-level declaration begins at the depth the namespace body sits at
    ///     (zero for a file-scoped namespace or none, one for a block namespace), and a type's own braces are
    ///     deeper. A declaration begins at the file's start or after a <c>;</c>, <c>{</c> or <c>}</c> at that depth,
    ///     past attribute lists and modifiers, which is why a constraint, a local called <c>record</c> and an
    ///     anonymous <c>delegate</c> are none. Comments and literals are blanked first, so quoted C# is not code.
    /// </remarks>
    private static IReadOnlyList<Declaration> TopLevelTypes(string source)
    {
        var text = SourceCommentStripper.StripCommentsAndLiterals(source);
        var baseDepth = BlockNamespaceDepth(text);
        var declarations = new List<Declaration>();
        var depth = 0;
        var atDeclarationStart = baseDepth == 0;
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (atDeclarationStart && depth == baseDepth)
            {
                if (current == '[')
                {
                    index = SkipBracketed(text, index);
                    continue;
                }

                if (IsWordCharacter(current))
                {
                    var word = ReadWord(text, index, out var after);

                    if (Modifiers.Contains(word))
                    {
                        index = after;
                        continue;
                    }

                    atDeclarationStart = false;
                    index = after;

                    if (TypeKeywords.Contains(word) && ReadDeclaration(text, word, after, out var next) is { } found)
                    {
                        declarations.Add(found);
                        index = next;
                    }

                    continue;
                }

                atDeclarationStart = false;
            }

            switch (current)
            {
                case '{':
                    depth++;
                    atDeclarationStart = depth == baseDepth;
                    break;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    atDeclarationStart = depth == baseDepth;
                    break;
                case ';':
                    atDeclarationStart = depth == baseDepth;
                    break;
                default:
                    break;
            }

            index++;
        }

        return declarations;
    }

    /// <summary>The brace depth a top-level declaration sits at: one inside a block namespace, zero otherwise.</summary>
    private static int BlockNamespaceDepth(string text)
    {
        const string Keyword = "namespace";

        for (var index = text.IndexOf(Keyword, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(Keyword, index + 1, StringComparison.Ordinal))
        {
            if (!IsWholeWord(text, index, Keyword.Length))
            {
                continue;
            }

            var head = text.IndexOfAny(['{', ';'], index);
            return head >= 0 && text[head] == '{' ? 1 : 0;
        }

        return 0;
    }

    /// <summary>Reads one declaration's head, or null when the keyword was not one.</summary>
    /// <remarks>
    ///     The head runs from just past the keyword to the <c>{</c> or <c>;</c> that ends it, and
    ///     <paramref name="next" /> is left on that character so the caller's depth tracking still sees it. A
    ///     keyword with no name after it — <c>record.Add(…)</c> on a local — is not a declaration.
    /// </remarks>
    private static Declaration? ReadDeclaration(string text, string keyword, int from, out int next)
    {
        var end = text.IndexOfAny(['{', ';'], from);
        next = end < 0 ? text.Length : end;
        var head = text[from..next];

        if (keyword == "delegate")
        {
            var handler = DelegateName(head);
            return handler.Length == 0 ? null : new Declaration("delegate", handler, string.Empty);
        }

        var name = ReadWord(head, SkipWhitespace(head, 0), out var after);

        // `record class` and `record struct` both declare a record; the kind keyword is not the name.
        if (keyword == "record" && name is "class" or "struct")
        {
            name = ReadWord(head, SkipWhitespace(head, after), out _);
        }

        if (name.Length == 0)
        {
            return null;
        }

        var colon = ColonOutsideBrackets(head);
        return new Declaration(keyword, name, colon < 0 ? string.Empty : head[(colon + 1)..]);
    }

    /// <summary>A delegate's name is the identifier carrying its parameter list, not the first in the head.</summary>
    /// <remarks>
    ///     The return type comes first and may itself be generic, so <c>delegate Func&lt;int&gt; Handler(…)</c> is
    ///     read by stepping back over the type-parameter list before the identifier.
    /// </remarks>
    private static string DelegateName(string head)
    {
        var open = head.IndexOf('(', StringComparison.Ordinal);

        if (open < 0)
        {
            return string.Empty;
        }

        var end = ClosesTypeParameters(head, open, out var opened) ? opened : open;

        while (end > 0 && !IsWordCharacter(head[end - 1]))
        {
            end--;
        }

        var start = end;

        while (start > 0 && IsWordCharacter(head[start - 1]))
        {
            start--;
        }

        return head[start..end];
    }

    /// <summary>Walks a <c>&lt;…&gt;</c> run backwards from <paramref name="from" />, if one closes there.</summary>
    private static bool ClosesTypeParameters(string head, int from, out int opened)
    {
        opened = from;
        var index = from - 1;

        while (index >= 0 && char.IsWhiteSpace(head[index]))
        {
            index--;
        }

        if (index < 0 || head[index] != '>')
        {
            return false;
        }

        var angles = 0;

        for (; index >= 0; index--)
        {
            angles += head[index] switch
            {
                '>' => 1,
                '<' => -1,
                _ => 0
            };

            if (angles == 0)
            {
                opened = index;
                return true;
            }
        }

        return false;
    }

    /// <summary>The <c>:</c> that opens a base list, ignoring any inside a type-argument or parameter list.</summary>
    private static int ColonOutsideBrackets(string head)
    {
        var depth = 0;

        for (var index = 0; index < head.Length; index++)
        {
            depth += head[index] switch
            {
                '<' or '(' or '[' => 1,
                '>' or ')' or ']' => -1,
                _ => 0
            };

            if (depth == 0 && head[index] == ':' && (index + 1 >= head.Length || head[index + 1] != ':'))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Steps over a balanced <c>[…]</c> run: an attribute list is never a declaration.</summary>
    private static int SkipBracketed(string text, int from)
    {
        var depth = 0;

        for (var index = from; index < text.Length; index++)
        {
            depth += text[index] switch
            {
                '[' => 1,
                ']' => -1,
                _ => 0
            };

            if (depth == 0)
            {
                return index + 1;
            }
        }

        return text.Length;
    }

    private static int SkipWhitespace(string text, int from)
    {
        var index = from;

        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static string ReadWord(string text, int from, out int after)
    {
        after = from;

        while (after < text.Length && IsWordCharacter(text[after]))
        {
            after++;
        }

        return text[from..after];
    }

    private static bool IsWholeWord(string text, int start, int length) =>
        (start == 0 || !IsWordCharacter(text[start - 1]))
        && (start + length >= text.Length || !IsWordCharacter(text[start + length]));

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private const string AllowlistHeader = """
                                           # Top-level type counts — the allowlist for FilePlacementConventionTests.
                                           #
                                           # Format: <repository-relative file>|<count>, one line per file, sorted. The count is how many top-level
                                           # types that file declares today, and every listed file declares at least two. A production file with no
                                           # line here must declare one type, or take one of the four exception shapes the test encodes:
                                           #   family file   *Dtos.cs, *Contracts.cs, *ServiceModels.cs, *Models.cs
                                           #   pair          exactly an interface IFoo and its implementation Foo
                                           #   satellites    one primary type beside only enums and delegates
                                           #   contract      I*Store.cs / I*Service.cs declaring that interface plus its own vocabulary
                                           # Test projects and generated/migration code are out of scope. Endpoints are not judged here at all:
                                           # EndpointConventionTests owns one-endpoint-per-file by reflection, and has no allowlist.
                                           #
                                           # The list is SHRINK-ONLY. Declaring more than the count fails; declaring less fails as stale, so the commit
                                           # that splits a file lowers or deletes its line in the same change and the room cannot be spent twice. To
                                           # rewrite the whole file after a cleanup batch, run the test with XE_FILE_PLACEMENT_SHRINK=1: it lowers
                                           # counts, drops emptied entries, never adds a key, never raises a count, and always fails afterwards.

                                           """;
}
