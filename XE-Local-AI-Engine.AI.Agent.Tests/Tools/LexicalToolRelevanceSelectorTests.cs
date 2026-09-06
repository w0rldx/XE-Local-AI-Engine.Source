namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shipped, model-free tool-relevance selector. Two properties matter more than the ranking itself: the fast
///     path at or below the threshold returns the whole array untouched (the byte-identical default), and the emitted
///     order is always the INPUT order, so a fixed selected set serialises to the same tools array on every round of a
///     turn — which is what keeps the llama.cpp prompt prefix and its compiled GBNF grammar stable.
/// </summary>
public sealed class LexicalToolRelevanceSelectorTests
{
    private const int Threshold = 12;

    [Test]
    public async Task SelectAsync_AtOrBelowTheThreshold_ReturnsEveryNameWithoutRanking()
    {
        var candidates = BuildNonCore(Threshold);

        var selection = await BuildSelector().SelectAsync("write a file", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(Threshold, selection.OfferedNames.Count, "At the threshold the whole array is offered.");
        AssertEx.Empty(selection.HiddenNames, "Nothing may be held back on the fast path.");
        AssertEx.True(selection.OfferedNames.SequenceEqual(candidates.Select(static candidate => candidate.Name), StringComparer.Ordinal),
            "The fast path returns the input order verbatim.");
    }

    [Test]
    public async Task SelectAsync_WithABlankQuery_ReturnsEveryName()
    {
        var candidates = BuildNonCore(Threshold + 8);

        var selection = await BuildSelector().SelectAsync("   ", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(candidates.Count, selection.OfferedNames.Count, "A blank query has nothing to rank against.");
        AssertEx.Empty(selection.HiddenNames);
    }

    [Test]
    public async Task SelectAsync_AboveTheThreshold_NeverTrimsACoreTool()
    {
        List<ToolRelevanceCandidate> candidates =
        [
            new("ask_user", "Asks the user a question.", IsCore: true),
            new("record_finding", "Records a finding.", IsCore: true),
            .. BuildNonCore(count: 20)
        ];

        var selection = await BuildSelector().SelectAsync("unrelated words", candidates, Threshold, CancellationToken.None);

        AssertEx.Contains(selection.OfferedNames, "ask_user");
        AssertEx.Contains(selection.OfferedNames, "record_finding");
        AssertEx.False(selection.HiddenNames.Contains("ask_user", StringComparer.Ordinal), "A core tool is never a ranking candidate.");
        AssertEx.False(selection.HiddenNames.Contains("record_finding", StringComparer.Ordinal), "A core tool is never a ranking candidate.");
    }

    [Test]
    public async Task SelectAsync_WhenEveryCandidateIsCoreAndExceedsTheThreshold_HidesNothing()
    {
        List<ToolRelevanceCandidate> candidates = [.. Enumerable.Range(0, 20).Select(index => new ToolRelevanceCandidate($"core_{index}", "A core tool.", IsCore: true))];

        var selection = await BuildSelector().SelectAsync("anything", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(candidates.Count, selection.OfferedNames.Count, "With nothing rankable the whole core is still offered.");
        AssertEx.Empty(selection.HiddenNames);
    }

    [Test]
    public async Task SelectAsync_WhenTheCoreLeavesFewerThanSixSlots_StillFillsSix()
    {
        // Core of nine — a skills-bearing work-session agent — leaves 12 - 9 = 3 slots by the bare threshold. The floor
        // raises the fill to six, so the ranker is always choosing among a meaningful set, and the offer may exceed the
        // threshold: the threshold is a trigger, not a cap.
        List<ToolRelevanceCandidate> candidates =
        [
            .. Enumerable.Range(0, 9).Select(index => new ToolRelevanceCandidate($"core_{index}", "A core tool.", IsCore: true)),
            .. BuildNonCore(count: 20)
        ];

        var selection = await BuildSelector().SelectAsync("read the file", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(expected: 15, selection.OfferedNames.Count, "Nine core plus the six-slot floor.");
        AssertEx.Equal(expected: 14, selection.HiddenNames.Count, "The remaining non-core tools are held back.");
    }

    [Test]
    public async Task SelectAsync_CalledTwiceWithTheSameInput_EmitsTheIdenticalOrder()
    {
        var candidates = BuildNonCore(count: 25);
        var selector = BuildSelector();

        var first = await selector.SelectAsync("read the knowledge base", candidates, Threshold, CancellationToken.None);
        var second = await selector.SelectAsync("read the knowledge base", candidates, Threshold, CancellationToken.None);

        AssertEx.True(first.OfferedNames.SequenceEqual(second.OfferedNames, StringComparer.Ordinal), "Selection is deterministic and re-sorted into input order.");
        AssertEx.True(first.HiddenNames.SequenceEqual(second.HiddenNames, StringComparer.Ordinal));
    }

    [Test]
    public async Task SelectAsync_WhenScoresTie_BreaksByTheCandidateIndex()
    {
        // Every candidate scores zero against a query sharing no token with any of them, so the tie-break — the input
        // index — is the whole ordering, and the first twelve win the twelve floored slots.
        var candidates = BuildNonCore(count: 30);

        var selection = await BuildSelector().SelectAsync("zzzz", candidates, Threshold, CancellationToken.None);

        AssertEx.True(selection.OfferedNames.SequenceEqual(candidates.Take(Threshold).Select(static candidate => candidate.Name), StringComparer.Ordinal),
            "An all-zero score set falls back to the input order.");
    }

    [Test]
    public async Task SelectAsync_AboveTheThreshold_PrefersTheCandidateTheQueryNames()
    {
        List<ToolRelevanceCandidate> candidates =
        [
            .. BuildNonCore(count: 20),
            new("search_knowledge_base", "Searches the node knowledge base for matching passages.", IsCore: false)
        ];

        var selection = await BuildSelector().SelectAsync("search the knowledge base for the pin", candidates, Threshold, CancellationToken.None);

        AssertEx.Contains(selection.OfferedNames, "search_knowledge_base", "The ranker exists to keep the tool the query is about.");
    }

    [Test]
    public async Task SelectAsync_WithAQueryOfNothingButFunctionWords_ReturnsEveryName()
    {
        // "the and to" tokenises to nothing once function words are dropped, which is the blank-query case wearing a
        // disguise: with no content word to rank WITH, every score is zero and a "ranking" would just be the input
        // order, so the whole array is offered instead of two thirds of it being hidden on a coin toss.
        var candidates = BuildNonCore(Threshold + 8);

        var selection = await BuildSelector().SelectAsync("Und dann, was ist mit der?", candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(candidates.Count, selection.OfferedNames.Count, "A query of pure function words has nothing to rank against.");
        AssertEx.Empty(selection.HiddenNames);
    }

    [Test]
    public async Task SelectAsync_OnTheLiveCatalogue_OffersTheToolTheQueryIsAbout()
    {
        // The C3 live round (2026-09-03, evidence 06-07-core-and-hatch.log). This is the real 20-tool catalogue of that
        // node with its real descriptions. The raw-overlap ranker scored search_knowledge_base 3 on "A", "THEN" and
        // "TO", Calculate / read_document / read_surrounding_chunks / spawn_subagent 2 apiece on the same function
        // words, and left custom__currency_convert — the one tool on the node that could answer — ranked ninth and
        // hidden. Nothing here may regress to that.
        var selection = await BuildSelector().SelectAsync("Convert 100 euros to dollars, then give me a stock quote.",
            BuildLiveCatalogue(),
            Threshold,
            CancellationToken.None);

        AssertEx.Contains(selection.OfferedNames, "custom__currency_convert", "The tool the query is literally about must be offered.");
        AssertEx.Contains(selection.OfferedNames, "custom__stock_quote", "The turn's second real match must be offered too.");
        AssertEx.True(selection.HiddenNames.Contains("search_knowledge_base", StringComparer.Ordinal),
            "A long description must no longer win a slot on 'a', 'then' and 'to'.");
    }

    [Test]
    public async Task SelectAsync_WhenALongNoisyDescriptionRepeatsTheQueryWords_LosesToTheShortExactMatch()
    {
        // Length normalisation, isolated: both candidates match both query words, so a raw count ties them and the
        // input index — which puts the noisy one first — would decide. Divided by the candidate's length, the tool that
        // is ABOUT converting currency beats the essay that merely mentions it.
        List<ToolRelevanceCandidate> candidates =
        [
            new("workspace_notes", $"A general workspace note reader. {string.Join(' ', Enumerable.Range(0, 60).Select(index => $"topic{index}"))} It can also convert a currency figure in passing.",
                IsCore: false),
            new("currency_convert", "Converts a currency amount between two currencies.", IsCore: false),
            .. BuildNonCore(count: 3)
        ];

        // One ranked slot, so the ordering itself is the assertion rather than the fill size.
        var selector = new LexicalToolRelevanceSelector(Options.Create(new ToolRelevanceOptions
        {
            MinimumRankedSlots = 1
        }));

        var selection = await selector.SelectAsync("convert currency", candidates, threshold: 1, CancellationToken.None);

        AssertEx.Contains(selection.OfferedNames, "currency_convert", "The shorter, exactly-matching description wins the only slot.");
        AssertEx.True(selection.HiddenNames.Contains("workspace_notes", StringComparer.Ordinal), "Volume alone must not win a slot.");
    }

    /// <summary>
    ///     The 20 tools the C3 live-validation node resolved, in resolution order, with the descriptions the model
    ///     actually saw. The four <c>custom__*</c> stubs are the harness's; every other description is the product's.
    /// </summary>
    private static List<ToolRelevanceCandidate> BuildLiveCatalogue()
    {
        const string Stub = "Test-only stub for the C3 tool-relevance live validation ({0}). Never invoked.";

        return
        [
            new("GetCurrentTime", "Returns the current UTC time, the local time, and today's date. Use it whenever the user asks what time or what day it is.", IsCore: false),
            new("Calculate", "Evaluates a basic arithmetic expression using +, -, *, / and parentheses, then returns the numeric result. Use it for any calculation the user asks for.", IsCore: false),
            new("list_files", "List files and folders in the read-only project workspace. Returns workspace-relative paths only; secrets and heavy generated directories are excluded.", IsCore: false),
            new("read_file", "Read a UTF-8 text file from the read-only project workspace. Optionally read a line range. Binary files are refused and oversized files are truncated.", IsCore: false),
            new("search_text", "Search the read-only project workspace for a text or regex pattern. Returns matches as relative/path:line: text; secret files and directories are excluded.",
                IsCore: false),
            new("search_knowledge_base",
                "Search the node-local knowledge base (the operator's own uploaded documents) for passages relevant to a question, and use ONLY the returned passages to ground a document-specific answer. "
                + "Prefer this tool whenever the question is about the operator's documents or local knowledge. Answering policy: rely solely on the retrieved passages for document-grounded claims; do not "
                + "invent facts or fill gaps from prior knowledge; if the results do not contain enough information to answer, say so plainly instead of guessing. Typical flow: search first, then pass the "
                + "returned collectionId to read_surrounding_chunks around a promising hit, or to read_document to read a whole document. Returns compact JSON hits with collectionId, documentId, chunkId, "
                + "content, score, and chunkIndex; an empty result set means the knowledge base has nothing matching the query.",
                IsCore: false),
            new("read_document",
                "Read a single knowledge-base document end to end by its documentId and collectionId (usually obtained from a search_knowledge_base hit). Returns the document's non-sensitive metadata plus "
                + "its ordered chunks. The content is bounded: a very large document is truncated and the result flags that truncation, so read the specific sections you need rather than assuming the whole "
                + "document is present. Use the returned passages only to ground document-specific claims; do not invent content that is not present.",
                IsCore: false),
            new("read_surrounding_chunks",
                "Read the chunks surrounding a specific chunk within a document, to recover context that straddles a chunk boundary. Identify the target by its documentId, collectionId, and the chunkIndex "
                + "of a search_knowledge_base hit, and request how many chunks to include before and after it. Returns the neighbor window in document order. Use the returned passages only to ground "
                + "document-specific claims.",
                IsCore: false),
            new("ask_user", "Asks the user a clarifying question and waits for the answer.", IsCore: true),
            new("spawn_subagent",
                "Spawn a sub-agent bound to a model to handle a delegated task and return its result. Provide exactly one of subAgentKey (a saved agent's id or name) or modelId (a model to bind directly). "
                + "Spawns are capacity-gated: a spawn that would exceed the node's memory or concurrency limits is declined with a reason. Sub-agents cannot themselves spawn.",
                IsCore: false),
            new("run_python", "Runs a Python snippet in the node sandbox and returns its output.", IsCore: true),
            new("update_work_plan", "Updates the work session's plan.", IsCore: true),
            new("record_finding", "Records a finding on the work session.", IsCore: true),
            new("save_artifact", "Saves an artifact on the work session.", IsCore: true),
            new("complete_work_session", "Completes the work session.", IsCore: true),
            new("custom__currency_convert", string.Format(CultureInfo.InvariantCulture, Stub, "currency_convert"), IsCore: false),
            new("custom__translate_text", string.Format(CultureInfo.InvariantCulture, Stub, "translate_text"), IsCore: false),
            new("custom__weather_lookup", string.Format(CultureInfo.InvariantCulture, Stub, "weather_lookup"), IsCore: false),
            new("custom__stock_quote", string.Format(CultureInfo.InvariantCulture, Stub, "stock_quote"), IsCore: false),
            new("list_tools", "Lists every tool the agent can call, including any held back from this turn's offer.", IsCore: true)
        ];
    }

    private static LexicalToolRelevanceSelector BuildSelector()
    {
        return new LexicalToolRelevanceSelector(Options.Create(new ToolRelevanceOptions()));
    }

    private static List<ToolRelevanceCandidate> BuildNonCore(int count)
    {
        return [.. Enumerable.Range(0, count).Select(index => new ToolRelevanceCandidate($"tool_{index}", $"Filler tool number {index}.", IsCore: false))];
    }
}
