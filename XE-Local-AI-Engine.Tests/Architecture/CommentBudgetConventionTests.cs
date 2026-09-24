namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Holds every enforced C# file to the comment and XML-doc budgets of
///     <c>docs/wiki/16-code-conventions.md</c>, against a shrink-only allowlist of what each file measures today.
/// </summary>
/// <remarks>
///     Nothing else in the build measures a summary's length, a comment run's height or a doc block's
///     well-formedness: <c>GenerateDocumentationFile</c> is unset, so the compiler never parses a <c>///</c> block
///     at all. The allowlist carries a COUNT per file and rule, not a per-item key: the items are prose, and a
///     count is the only key rewording cannot break. Test projects are ratcheted exactly like production, which
///     keeps the same growth out of them, while production measures zero for the four shape rules.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class CommentBudgetConventionTests
{
    private const int SummaryBudget = 240;
    private const int TagBudget = 160;
    private const int RemarksLineBudget = 5;
    private const int RemarksCharBudget = 600;
    private const int DocBlockLineBudget = 15;
    private const int RunLineBudget = 2;

    /// <summary>Set to <c>1</c> to rewrite the allowlist down to what the tree measures. Always fails afterwards.</summary>
    private const string ShrinkVariable = "XE_COMMENT_BUDGET_SHRINK";

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "CommentBudgetAllowlist.txt");

    /// <summary>
    ///     A floor a little under today's file count, which the cleanup batches do not lower. A walk that lost
    ///     its roots reads too few files and fails here rather than reporting a clean tree.
    /// </summary>
    private const int EnforcedFileFloor = 4200;

    /// <summary>The rule names, from the measurement itself, so a rule cannot exist without a key.</summary>
    private static readonly string[] Rules = [.. Pairs(default).Select(pair => pair.Rule)];

    /// <summary>The scan is pure and every test needs all of it, so one walk serves them.</summary>
    private static readonly Lazy<(int Files, HashSet<string> Scanned, Dictionary<string, int> Measured)> Scan =
        new(Walk, isThreadSafe: true);

    /// <summary>
    ///     Snippets the scanner must count, one over-budget item each. Anything it stops seeing here it stops
    ///     seeing everywhere, and the ratchet then reports a tree that only got quieter.
    /// </summary>
    private static readonly (string Case, string Source, string Rule)[] MustCount =
    [
        ("an over-long summary", Doc($"<summary>{Filler(260)}</summary>"), "summary"),
        ("a summary spread over several lines", Doc("<summary>", Filler(130), Filler(130), "</summary>"), "summary"),
        ("an over-long param", Doc($"<param name=\"value\">{Filler(170)}</param>"), "tag"),
        ("an over-long returns", Doc($"<returns>{Filler(170)}</returns>"), "tag"),
        ("an over-long value", Doc($"<value>{Filler(170)}</value>"), "tag"),
        ("a second returns that is the over-long one",
            Doc("<returns>short</returns>", $"<returns>{Filler(170)}</returns>"), "tag"),
        ("a second value that is the over-long one",
            Doc("<value>short</value>", $"<value>{Filler(170)}</value>"), "tag"),
        ("a second param that is the over-long one",
            Doc("<param name=\"a\">short</param>", $"<param name=\"b\">{Filler(170)}</param>"), "tag"),
        ("a remarks over its line budget",
            Doc(Ok, "<remarks>", "one", "two", "three", "four", "five", "six", "</remarks>"), "remarks"),
        ("a remarks over its character budget", Doc(Ok, $"<remarks>{Filler(620)}</remarks>"), "remarks"),
        ("a nested remarks counted by its raw lines",
            Doc(Ok, "<remarks>", "<para>", "one", "two", "three", "four", "</para>", "</remarks>"), "remarks"),
        ("a doc block over its line budget", Doc([.. Enumerable.Repeat("line", 16)]), "block"),
        ("a block carrying two remarks",
            Doc(Ok, "<remarks>one</remarks>", "<remarks>two</remarks>"), "multi"),
        ("a block carrying two summaries", Doc(Ok, "<summary>second</summary>"), "multi"),
        ("a seealso used as an overflow slot for prose",
            Doc(Ok, "<seealso cref=\"X\">Why this member is the way it is.</seealso>"), "seealso"),
        ("a remarks with no summary to belong to", Doc("<remarks>one</remarks>"), "nosummary"),
        ("a remarks closed by a summary tag", Doc("<remarks>one</summary>"), "xml"),
        ("an orphan closing tag", Doc("A sentence.", "</para>"), "xml"),
        ("a bare ampersand in a summary", Doc("<summary>read & write</summary>"), "xml"),
        ("a bare angle bracket in prose", Doc("<summary>anything under 5 % is noise</summary>", "a < b"), "xml"),
        ("a three-line own-line comment run", "// one\n// two\n// three\nCode();", "run"),
        ("a run that starts the file", "// one\n// two\n// three", "run"),
        ("a run indented under code", "class X\n{\n    // one\n    // two\n    // three\n    void M() { }\n}", "run"),
        ("a quadruple-slash run, which C# reads as ordinary comments",
            "//// one\n//// two\n//// three\nCode();", "run")
    ];

    /// <summary>Text that only looks over budget. A guard that fires on these is one somebody deletes.</summary>
    private static readonly (string Case, string Source)[] MustNotCount =
    [
        ("a summary at the budget", Doc($"<summary>{Filler(240)}</summary>")),
        ("a param at the budget", Doc($"<param name=\"value\">{Filler(160)}</param>")),
        ("a doc block at the line budget", Doc([.. Enumerable.Repeat("line", 15)])),
        ("a remarks at both budgets", Doc(Ok, "<remarks>", "one", "two", "three", "four", "five", "</remarks>")),
        ("a two-line run", "// one\n// two\nCode();"),
        ("a run interrupted by a blank line", "// one\n// two\n\n// three\n// four\nCode();"),
        ("a run interrupted by a code line", "// one\n// two\nCode();\n// three\n// four"),
        ("an end-of-line comment above own-line ones", "Code(); // one\n// two\n// three"),
        ("three end-of-line comments", "A(); // one\nB(); // two\nC(); // three"),
        ("a URL in a string literal", "var a = \"https://x\";\nvar b = \"https://y\";\nvar c = \"https://z\";"),
        ("a raw string holding comment lines",
            "var a = \"\"\"\n// one\n// two\n// three\n\"\"\";"),
        ("a raw string holding a doc line and a long summary",
            $"var a = \"\"\"\n/// <summary>{Filler(300)}</summary>\n\"\"\";"),
        ("an interpolated string holding comment lines",
            "var a = $\"\"\"\n// one\n// two\n// three {Value}\n\"\"\";"),
        ("a block comment holding comment lines", "/*\n// one\n// two\n// three\n*/"),
        ("an over-long block comment", "/*\n" + string.Join("\n", Enumerable.Repeat("prose", 20)) + "\n*/"),
        ("an inheritdoc block", Doc("<inheritdoc />")),
        ("a summary with element children", Doc($"<summary>{Filler(120)} <see cref=\"X\" /> <c>y</c></summary>")),
        ("a summary whose markup, not its prose, passes the budget",
            Doc($"<summary>{Filler(200)} <see cref=\"A.Long.Namespace.And.TypeName\" /> "
                + "<see cref=\"A.Long.Namespace.And.TypeName\" /></summary>")),
        ("two doc blocks split by a blank line",
            string.Join('\n', Enumerable.Repeat("/// line", 9))
            + "\n\n" + string.Join('\n', Enumerable.Repeat("/// line", 9))),
        ("a doc block split by a code line",
            string.Join('\n', Enumerable.Repeat("/// line", 9))
            + "\nvoid M() { }\n" + string.Join('\n', Enumerable.Repeat("/// line", 9))),
        ("a self-closing seealso", Doc(Ok, "<seealso cref=\"X\" />")),
        ("a seealso with an empty body", Doc(Ok, "<seealso href=\"../x.md\"></seealso>")),
        ("a block of tags documenting a member documented elsewhere",
            Doc("<param name=\"value\">Read once by the caller.</param>", "<returns>The resolved value.</returns>")),
        ("an inheritdoc with a cref carrying its own remarks",
            Doc("<inheritdoc cref=\"IThing.Do\" />", "<remarks>one</remarks>")),
        ("a generic cref, a typeparamref and escaped entities",
            Doc("<summary>Wraps a <see cref=\"List{T}\" /> of <typeparamref name=\"T\" /> while "
                + "<c>a &lt; b</c> and <c>x &amp;&amp; y</c>.</summary>",
                "<typeparam name=\"T\">The element type.</typeparam>")),
        ("a remarks built from para and list markup",
            Doc(Ok, "<remarks>", "<para>one</para>",
                "<list type=\"bullet\"><item><description>two</description></item></list>", "</remarks>"))
    ];

    [Test]
    public void EnforcedProjects_StayWithinTheCommentBudgets()
    {
        var (_, scanned, measured) = Scan.Value;
        var allowed = Allowlist();

        if (string.Equals(Environment.GetEnvironmentVariable(ShrinkVariable), "1", StringComparison.Ordinal))
        {
            Shrink(allowed, measured, scanned);

            AssertEx.True(false,
                $"{ShrinkVariable} rewrote Architecture/CommentBudgetAllowlist.txt down to what the tree measures. "
                + "The rewrite never adds a key and never raises a count, and it always fails so that a "
                + "regeneration can never be mistaken for a green run. Review the diff, unset the variable and "
                + "re-run.");
        }

        var offenders = measured
                        .Where(entry => entry.Value > allowed.GetValueOrDefault(entry.Key))
                        .Select(entry => $"{entry.Key}|{entry.Value}   (allowed {allowed.GetValueOrDefault(entry.Key)})")
                        .Order(StringComparer.Ordinal)
                        .ToList();

        AssertEx.Empty(offenders,
            "A comment or XML-doc rule from docs/wiki/16-code-conventions.md grew: a summary over "
            + $"{SummaryBudget} characters, a param/returns/value over {TagBudget}, a remarks over "
            + $"{RemarksLineBudget} content lines or {RemarksCharBudget} characters, a doc block over "
            + $"{DocBlockLineBudget} lines, an own-line // run over {RunLineBudget} lines, a block carrying a "
            + "second summary or remarks (multi), a seealso carrying prose (seealso), a remarks with no summary "
            + "to belong to (nosummary), or a block that is not well-formed XML (xml). The allowlist is "
            + "SHRINK-ONLY, so the fix is to bring the doc inside the rule — not to raise the count. Each "
            + "line below reads file|rule|measured:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    ///     The shrink-only half. Without it the list keeps every count it was ever seeded with, and a file that was
    ///     cleaned three batches ago silently re-earns the room it gave up.
    /// </summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var (_, scanned, measured) = Scan.Value;
        var stale = new List<string>();

        foreach (var (key, allowed) in Allowlist())
        {
            var file = key[..key.IndexOf('|', StringComparison.Ordinal)];

            if (!scanned.Contains(file))
            {
                stale.Add($"{key}|{allowed}   -> delete the line, '{file}' is no longer scanned");
                continue;
            }

            var actual = measured.GetValueOrDefault(key);

            if (actual < allowed)
            {
                stale.Add(actual == 0
                    ? $"{key}|{allowed}   -> delete the line"
                    : $"{key}|{allowed}   -> write {key}|{actual}");
            }
        }

        AssertEx.Empty(stale,
            "Architecture/CommentBudgetAllowlist.txt reserves more room than the tree uses. The list is "
            + "shrink-only: the commit that cleans a file lowers or deletes its line in the same change, so the "
            + $"room cannot be spent again. Apply the replacements below, or re-run with {ShrinkVariable}=1 to "
            + "rewrite the whole file:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     The guard's own check, and the proof that every rule still fires: a scanner that read quoted text as a
    ///     comment, or stopped recognising a construct, reports a budget nobody is holding.
    /// </summary>
    [Test]
    public void TheGuard_CountsRealCommentsAndIgnoresLookalikes()
    {
        var uncovered = Rules
                        .Where(rule => !MustCount.Any(entry => string.Equals(entry.Rule, rule, StringComparison.Ordinal)))
                        .ToList();

        AssertEx.Empty(uncovered,
            "A rule has no case it must count, so nothing proves it still recognises its construct and an "
            + "allowlist it has stopped feeding reads as a clean tree. Add a snippet for each rule below:"
            + Environment.NewLine + string.Join(Environment.NewLine, uncovered));

        foreach (var (name, source, rule) in MustCount)
        {
            AssertEx.Equal($"{rule}=1",
                string.Join(", ", Pairs(Counts(source)).Where(pair => pair.Count > 0).Select(pair => $"{pair.Rule}={pair.Count}")),
                $"The '{name}' case was not counted as exactly one '{rule}'. A miss here is how this ratchet stops "
                + "seeing prose.");
        }

        foreach (var (name, source) in MustNotCount)
        {
            AssertEx.Equal(string.Empty,
                string.Join(", ", Pairs(Counts(source)).Where(pair => pair.Count > 0).Select(pair => $"{pair.Rule}={pair.Count}")),
                $"The '{name}' case was counted as over budget. A guard that fires on quoted text, on a comment "
                + "that is inside its budget, or on a run that a blank line already ended is one somebody deletes.");
        }
    }

    /// <summary>
    ///     Non-vacuity. The budget assertions pass on an empty scan, so the scan itself is asserted to be large.
    /// </summary>
    [Test]
    public void TheScan_ReadsTheWholeEnforcedTree()
    {
        var (files, _, _) = Scan.Value;

        AssertEx.True(files >= EnforcedFileFloor,
            $"Only {files} C# files were scanned in the enforced projects, below the floor of "
            + $"{EnforcedFileFloor}. The walk is reading the wrong directories, so this ratchet cannot fire.");
    }

    // ---------------------------------------------------------------- the allowlist

    /// <summary>
    ///     Reads <c>file|rule|count</c> into a map keyed <c>file|rule</c>, from the repository rather than the test
    ///     output directory, so there is no <c>CopyToOutputDirectory</c> to keep in step.
    /// </summary>
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

            if (fields.Length != 3
                || !Rules.Contains(fields[1], StringComparer.Ordinal)
                || !int.TryParse(fields[2], out var count)
                || count <= 0
                || !entries.TryAdd($"{fields[0]}|{fields[1]}", count))
            {
                throw new InvalidDataException($"Architecture/CommentBudgetAllowlist.txt line {number} is not a unique "
                                               + $"'file|rule|positive count' entry: {line}");
            }
        }

        return entries;
    }

    /// <summary>
    ///     Rewrites the allowlist as <c>min(allowed, measured)</c> per existing key, dropping the keys that reached
    ///     zero or left the tree. It can only ever shrink: a key the file does not already hold is never added.
    /// </summary>
    private static void Shrink(Dictionary<string, int> allowed,
        Dictionary<string, int> measured,
        HashSet<string> scanned)
    {
        var text = new StringBuilder(AllowlistHeader);

        var kept = allowed
                   .Where(entry => scanned.Contains(entry.Key[..entry.Key.IndexOf('|', StringComparison.Ordinal)]))
                   .Select(entry => (entry.Key, Count: Math.Min(entry.Value, measured.GetValueOrDefault(entry.Key))))
                   .Where(entry => entry.Count > 0)
                   .OrderBy(entry => entry.Key, StringComparer.Ordinal);

        foreach (var (key, count) in kept)
        {
            text.Append(key).Append('|').Append(count).Append('\n');
        }

        File.WriteAllText(AllowlistPath, text.ToString());
    }

    // ---------------------------------------------------------------- scanning

    private static (int Files, HashSet<string> Scanned, Dictionary<string, int> Measured) Walk()
    {
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        var measured = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (path, relative) in EnforcedSourceFiles.All())
        {
            scanned.Add(relative);

            foreach (var (rule, count) in Pairs(Counts(File.ReadAllText(path))).Where(pair => pair.Count > 0))
            {
                measured[$"{relative}|{rule}"] = count;
            }
        }

        return (scanned.Count, scanned, measured);
    }

    /// <summary>How many items in <paramref name="source" /> break each budget or shape rule.</summary>
    /// <remarks>
    ///     Comment recognition comes from <see cref="SourceCommentStripper" />, so a <c>//</c> or <c>///</c> inside
    ///     a regular, verbatim, interpolated or raw literal is content and never an item. A doc block is a run of
    ///     consecutive own-line <c>///</c> lines; a <c>//</c> run is a run of consecutive own-line <c>//</c> lines,
    ///     which a blank line, a code line, an end-of-line comment or a doc line all end.
    /// </remarks>
    private static Counted Counts(string source)
    {
        var starts = LineStarts(source);
        var docs = new SortedDictionary<int, string>();
        var runs = new SortedSet<int>();

        foreach (var span in SourceCommentStripper.CommentSpans(source))
        {
            var (start, length) = span.GetOffsetAndLength(source.Length);

            if (source[start + 1] != '/')
            {
                continue;
            }

            var line = LineOf(starts, start);

            if (!source.AsSpan(starts[line - 1], start - starts[line - 1]).IsWhiteSpace())
            {
                continue;
            }

            var body = source[(start + 2)..(start + length)];

            if (IsDocLine(body))
            {
                docs[line] = DocContent(body);
            }
            else
            {
                runs.Add(line);
            }
        }

        var (block, multi, noSummary, remarks, seealso, summary, tag, xml) = (0, 0, 0, 0, 0, 0, 0, 0);

        foreach (var group in Consecutive(docs.Keys))
        {
            if (group.Count > DocBlockLineBudget)
            {
                block++;
            }

            var joined = string.Join('\n', group.Select(line => docs[line]));

            summary += Over(SummaryTag(), joined, SummaryBudget) ? 1 : 0;
            tag += OverTagBudget(joined) ? 1 : 0;
            remarks += OverRemarksBudget(joined) ? 1 : 0;

            var parsed = Parsed(joined);

            if (parsed is null)
            {
                xml++;
                continue;
            }

            multi += HasRepeatedElement(parsed) ? 1 : 0;
            noSummary += HasRemarksWithoutSummary(parsed) ? 1 : 0;
            seealso += HasProseSeealso(parsed) ? 1 : 0;
        }

        var run = Consecutive(runs).Count(group => group.Count > RunLineBudget);

        return new Counted(block, multi, noSummary, remarks, run, seealso, summary, tag, xml);
    }

    /// <summary>
    ///     The block wrapped in a root and parsed, or <see langword="null" /> when it is not well-formed XML.
    /// </summary>
    /// <remarks>
    ///     Nothing else in the build reads a <c>///</c> block as XML, so a <c>&lt;remarks&gt;</c> closed by
    ///     <c>&lt;/summary&gt;</c>, an orphan closer or a bare <c>&amp;</c> ships unnoticed — and every shape rule
    ///     below is a question about elements, which only a parse can answer. A malformed block is reported as
    ///     <c>xml</c> alone; fixing it is what exposes it to the other three.
    /// </remarks>
    private static XElement? Parsed(string joined)
    {
        try
        {
            return XElement.Parse($"<doc>{joined}</doc>");
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    ///     A block documenting one member twice. The length rules read the FIRST occurrence, so splitting an
    ///     over-long element in two passed them; a second element is now a violation of its own.
    /// </summary>
    private static bool HasRepeatedElement(XElement block) =>
        block.Elements("summary").Skip(1).Any() || block.Elements("remarks").Skip(1).Any();

    /// <summary>A cross-reference carrying a body is prose in a slot no budget measures.</summary>
    private static bool HasProseSeealso(XElement block) =>
        block.Descendants("seealso").Any(element => !string.IsNullOrWhiteSpace(element.Value));

    /// <summary>
    ///     Detail with nothing to detail: a block explaining a member it never names. A block of <c>param</c> or
    ///     <c>returns</c> tags alone is the documented-elsewhere shape and is not this rule.
    /// </summary>
    private static bool HasRemarksWithoutSummary(XElement block) =>
        block.Elements("remarks").Any()
        && !block.Elements("summary").Any()
        && !block.Elements("inheritdoc").Any();

    /// <summary>A third slash makes a documentation line; a fourth takes it back to an ordinary comment.</summary>
    private static bool IsDocLine(string body) =>
        body.StartsWith('/') && (body.Length < 2 || body[1] != '/');

    /// <summary>The text after <c>///</c>, with the one separating space that is not content removed.</summary>
    private static string DocContent(string body)
    {
        var content = body[1..].TrimEnd();
        return content.StartsWith(' ') ? content[1..] : content;
    }

    /// <summary>
    ///     The FIRST occurrence, which <c>multi</c> is what makes safe: a second <c>&lt;summary&gt;</c> or
    ///     <c>&lt;remarks&gt;</c> is now a violation in its own right, so splitting an over-long one in two no
    ///     longer buys an unmeasured slot.
    /// </summary>
    private static bool Over(Regex tag, string joined, int budget)
    {
        var match = tag.Match(joined);
        return match.Success && Flatten(match.Groups[1].Value).Length > budget;
    }

    /// <summary>
    ///     Every occurrence, not the first: a block carrying two <c>&lt;returns&gt;</c> is malformed, but reading
    ///     only the first one lets the second grow without limit and the rule silently stops covering it.
    /// </summary>
    private static bool AnyOver(Regex tag, string joined, int budget) =>
        tag.Matches(joined).Any(match => Flatten(match.Groups[1].Value).Length > budget);

    private static bool OverTagBudget(string joined) =>
        AnyOver(ParamTag(), joined, TagBudget)
        || AnyOver(ReturnsTag(), joined, TagBudget)
        || AnyOver(ValueTag(), joined, TagBudget);

    private static bool OverRemarksBudget(string joined)
    {
        var match = RemarksTag().Match(joined);

        if (!match.Success)
        {
            return false;
        }

        var raw = match.Groups[1].Value;
        var lines = raw.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));

        return lines > RemarksLineBudget || Flatten(raw).Length > RemarksCharBudget;
    }

    /// <summary>Markup is structure, not prose: a budget counts what a reader reads.</summary>
    private static string Flatten(string text) =>
        Whitespace().Replace(AnyTag().Replace(text, " "), " ").Trim();

    private static IEnumerable<List<int>> Consecutive(IEnumerable<int> lines)
    {
        var group = new List<int>();

        foreach (var line in lines)
        {
            if (group.Count > 0 && line != group[^1] + 1)
            {
                yield return group;
                group = [];
            }

            group.Add(line);
        }

        if (group.Count > 0)
        {
            yield return group;
        }
    }

    private static int[] LineStarts(string source)
    {
        var starts = new List<int>
        {
            0
        };

        for (var index = source.IndexOf('\n', StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf('\n', index + 1))
        {
            starts.Add(index + 1);
        }

        return [.. starts];
    }

    private static int LineOf(int[] starts, int offset)
    {
        var found = Array.BinarySearch(starts, offset);
        return found >= 0 ? found + 1 : ~found;
    }

    /// <summary>One file's count per rule. A rule that is not a member here cannot reach the allowlist.</summary>
    private readonly record struct Counted(
        int Block,
        int Multi,
        int NoSummary,
        int Remarks,
        int Run,
        int Seealso,
        int Summary,
        int Tag,
        int Xml);

    private static (string Rule, int Count)[] Pairs(Counted counts) =>
    [
        ("block", counts.Block), ("multi", counts.Multi), ("nosummary", counts.NoSummary),
        ("remarks", counts.Remarks), ("run", counts.Run), ("seealso", counts.Seealso),
        ("summary", counts.Summary), ("tag", counts.Tag), ("xml", counts.Xml)
    ];

    // ---------------------------------------------------------------- self-check fixtures

    /// <summary>A minimal well-formed summary, for the cases whose subject is another element entirely.</summary>
    private const string Ok = "<summary>ok</summary>";

    private static string Doc(params string[] lines) =>
        string.Join('\n', lines.Select(line => $"/// {line}")) + "\nvoid M() { }";

    private static string Filler(int length) =>
        new('x', length);

    [GeneratedRegex("<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryTag();

    [GeneratedRegex("""<param\s+name="[^"]*"[^>]*>(.*?)</param>""", RegexOptions.Singleline)]
    private static partial Regex ParamTag();

    [GeneratedRegex("<returns>(.*?)</returns>", RegexOptions.Singleline)]
    private static partial Regex ReturnsTag();

    [GeneratedRegex("<value>(.*?)</value>", RegexOptions.Singleline)]
    private static partial Regex ValueTag();

    [GeneratedRegex("<remarks>(.*?)</remarks>", RegexOptions.Singleline)]
    private static partial Regex RemarksTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private const string AllowlistHeader = """
                                           # Comment and XML-doc budget counts — the allowlist for CommentBudgetConventionTests.
                                           #
                                           # Format: <repository-relative file>|<rule>|<count>, one line per file and rule, sorted. The count is how
                                           # many items in that file exceed that budget today. A file with no line for a rule must measure zero.
                                           # Blank lines and lines starting with '#' are ignored.
                                           #
                                           # Rules (docs/wiki/16-code-conventions.md). The first five are length budgets, the last four are shapes
                                           # that satisfy a length budget while being wrong:
                                           #   summary   a <summary> over 240 characters of tag-stripped text
                                           #   tag       a <param>/<returns>/<value> over 160 characters
                                           #   remarks   a <remarks> over 5 content lines or 600 characters
                                           #   block     a /// block over 15 lines
                                           #   run       more than 2 consecutive own-line // lines
                                           #   multi     a /// block carrying a second <summary> or <remarks>
                                           #   seealso   a <seealso> carrying a prose body, which no budget measures
                                           #   nosummary a /// block with <remarks> but no <summary> and no <inheritdoc>
                                           #   xml       a /// block that is not well-formed XML; nothing else in the build parses one
                                           #
                                           # The list is SHRINK-ONLY. Measuring more than the count fails; measuring less fails as stale, so the commit
                                           # that cleans a file lowers or deletes its line in the same change and the room cannot be spent twice. To
                                           # rewrite the whole file after a cleanup batch, run the test with XE_COMMENT_BUDGET_SHRINK=1: it lowers
                                           # counts, drops emptied entries, never adds a key, never raises a count, and always fails afterwards.

                                           """;
}
