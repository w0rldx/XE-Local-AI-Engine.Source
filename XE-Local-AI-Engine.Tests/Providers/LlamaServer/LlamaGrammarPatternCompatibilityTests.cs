namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Every JSON Schema <c>pattern</c> the node can offer a llama.cpp model must survive that converter's regex
///     subset. This is a SILENT-corruption gate, not a crash gate: an oversized repetition bound fails the request
///     loudly (that is <see cref="LlamaGrammarToolSchemaCompatibilityTests" />'s subject and the sanitizer's job), but
///     an unsupported regex construct compiles into a grammar that is merely WRONG — and grammar-constrained decoding
///     then forces every model on that node to emit values the tool's own validator rejects. A live round is currently
///     the only other way to notice.
///     <para>
///         <b>Ground truth.</b> The rules below come from llama.cpp <c>common/json-schema-to-grammar.cpp</c>
///         <c>_visit_pattern</c> at the pinned build (b10201, commit <c>8f4646a</c>), read and then exercised by
///         compiling that translation unit standalone and running it over each construct:
///         <list type="bullet">
///             <item>
///                 <description>
///                     It requires <c>pattern.front() == '^' &amp;&amp; pattern.back() == '$'</c> and then takes
///                     <c>substr(1, length - 2)</c> — exactly one anchor off each end. A pattern missing either is a
///                     hard conversion error ("Pattern must start with '^' and end with '$'").
///                 </description>
///             </item>
///             <item>
///                 <description>
///                     Its <c>NON_LITERAL_SET</c> is <c>{'|', '.', '(', ')', '[', ']', '{', '}', '*', '+', '?'}</c> —
///                     <c>^</c> and <c>$</c> are ABSENT, so an interior anchor falls through to the literal branch and
///                     is emitted as a character the model must produce. <c>^a$|^b$</c> compiles to
///                     <c>("a$" | "^b")</c>. This is the defect that blocked the first AgentHome live round.
///                 </description>
///             </item>
///             <item>
///                 <description>
///                     <c>(?:</c> is supported (skipped, treated as a plain group). Every other <c>(?</c> form —
///                     lookahead/lookbehind — only warns and then SKIPS THE WHOLE GROUP, silently dropping it from the
///                     grammar.
///                 </description>
///             </item>
///             <item>
///                 <description>
///                     A backslash escape whose next character is not in
///                     <c>ESCAPED_IN_REGEXPS_BUT_NOT_IN_LITERALS</c> is copied through verbatim, so <c>\d</c>, <c>\w</c>,
///                     <c>\s</c>, <c>\b</c> and a backreference <c>\1</c> become the two-character GBNF literals
///                     <c>"\d"</c>, <c>"\w"</c>, <c>"\s"</c>, <c>"\b"</c>, <c>"\1"</c> rather than classes, word
///                     boundaries or backreferences. (An ESCAPED anchor, <c>\^</c> or <c>\$</c>, is handled correctly
///                     and stays a literal — so the interior-anchor rule below must not flag it.)
///                 </description>
///             </item>
///         </list>
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LlamaGrammarPatternCompatibilityTests
{
    [Test]
    public void EveryOfferablePattern_CompilesUnderTheLlamaCppRegexSubset()
    {
        // The widest real offer: the profile pool (built-ins + coder + knowledge + ask_user + spawn + run_python +
        // run_in_agent_home + the four work-session tools) unioned with emit_output, built on the REAL registry and the
        // REAL provider. The same artifact the wire-body and live-smoke tests grade, so a tool added to the offer joins
        // this gate automatically rather than needing to be remembered.
        var offenders = new List<string>();
        var scanned = new List<string>();

        foreach (var tool in LlamaGrammarToolOffer.BuildProductionToolOffer())
        {
            CollectPatternOffences(tool.Name, tool.JsonSchema, scanned, offenders);
        }

        AssertEx.Empty(offenders,
            "a tool schema carries a regex pattern llama.cpp's json-schema-to-grammar would mis-compile: "
            + string.Join(" | ", offenders));

        // An all-clear is only worth something if the walk actually reached a pattern. run_in_agent_home's
        // selectedFolderIds is the one the offer carries today and the one the live round broke on, so name it: a
        // refactor that stopped descending into tool schemas would otherwise leave this gate green and empty forever.
        AssertEx.Contains(scanned, AgentHomeRunToolRequestValidator.SelectedFolderIdPattern);
    }

    [Test]
    public void TheGuard_RejectsTheShapeThatBlockedTheAgentHomeLiveRound()
    {
        // The guard's own negative control. Without this, a bug that made DescribePatternOffence always return null
        // would leave the test above green and vacuous forever.
        AssertEx.NotNull(DescribePatternOffence("^[a-z0-9][a-z0-9-]{0,63}$|^[0-9a-fA-F-]{36}$"),
            "two separately anchored alternatives is exactly the shape that compiled a literal '$' into the grammar");
        AssertEx.NotNull(DescribePatternOffence("abc"));
        AssertEx.NotNull(DescribePatternOffence("^abc"));
        AssertEx.NotNull(DescribePatternOffence("^(?=x)ab$"));
        AssertEx.NotNull(DescribePatternOffence("^(a)\\1$"));
        AssertEx.NotNull(DescribePatternOffence("^\\d{3}$"));
        AssertEx.NotNull(DescribePatternOffence("^\\bword\\b$"));

        // …and the shapes it must NOT flag, or the guard would block correct schemas.
        AssertEx.Null(DescribePatternOffence("^([a-z0-9][a-z0-9-]{0,63}|[0-9a-fA-F-]{36})$"), "the fixed AgentHome pattern");
        AssertEx.Null(DescribePatternOffence("^abc$"));
        AssertEx.Null(DescribePatternOffence("^(?:ab)+$"), "a non-capturing group is supported");
        AssertEx.Null(DescribePatternOffence("^a|b$"), "a top-level alternation under the outer anchors is fine");
        AssertEx.Null(DescribePatternOffence("^[a-z]+@[a-z]+\\.[a-z]{2,}$"), "an escaped dot is compiled correctly");
        AssertEx.Null(DescribePatternOffence("^a\\$b$"), "an ESCAPED anchor is a literal and is handled correctly");
    }

    private static void CollectPatternOffences(string toolName, JsonElement schema, List<string> scanned, List<string> offenders)
    {
        switch (schema.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in schema.EnumerateObject())
                {
                    if (string.Equals(property.Name, "pattern", StringComparison.Ordinal)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var pattern = property.Value.GetString()!;
                        scanned.Add(pattern);
                        if (DescribePatternOffence(pattern) is { } offence)
                        {
                            offenders.Add($"{toolName}: '{pattern}' — {offence}");
                        }
                    }

                    CollectPatternOffences(toolName, property.Value, scanned, offenders);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in schema.EnumerateArray())
                {
                    CollectPatternOffences(toolName, item, scanned, offenders);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    ///     Describes why llama.cpp's converter would mis-compile <paramref name="pattern" />, or <see langword="null" />
    ///     when it compiles faithfully. Deliberately a hand-rolled scan rather than a regex over a regex: it has to
    ///     distinguish an escaped anchor from a real one, which means tracking backslashes and character classes.
    /// </summary>
    private static string? DescribePatternOffence(string pattern)
    {
        if (pattern.Length < 2 || pattern[0] != '^' || pattern[^1] != '$')
        {
            return "must start with '^' and end with '$' — the converter rejects anything else outright";
        }

        var body = pattern[1..^1];
        var insideClass = false;

        // A while loop, not a for: an escape pair consumes two characters, and advancing the cursor from inside a for
        // body is what S127 forbids.
        var index = 0;
        while (index < body.Length)
        {
            var current = body[index];
            index++;

            if (current == '\\' && index < body.Length)
            {
                var escaped = body[index];
                index++;

                if (escaped is 'd' or 'D' or 'w' or 'W' or 's' or 'S' && !insideClass)
                {
                    return $"'\\{escaped}' is copied through as a literal, not a character class";
                }

                if (escaped is 'b' or 'B' or 'A' or 'Z' or 'z' or 'G')
                {
                    return $"'\\{escaped}' is copied through as a literal, not a zero-width assertion";
                }

                if (escaped is >= '1' and <= '9')
                {
                    return $"backreference '\\{escaped}' is copied through as a literal";
                }

                continue;
            }

            if (current == '[')
            {
                insideClass = true;
                continue;
            }

            if (current == ']')
            {
                insideClass = false;
                continue;
            }

            if (insideClass)
            {
                continue;
            }

            if (current is '^' or '$')
            {
                return $"interior '{current}' is compiled as a LITERAL character — put the alternation inside one pair of anchors, '^(a|b)$'";
            }

            // index already points past `current`, so body[index] is the character after the '('.
            if (current == '(' && index < body.Length && body[index] == '?'
                && !(index + 1 < body.Length && body[index + 1] == ':'))
            {
                return "lookaround '(?' is unsupported — the converter warns and drops the whole group";
            }
        }

        return null;
    }
}
