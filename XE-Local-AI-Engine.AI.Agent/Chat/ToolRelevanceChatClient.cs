namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

/// <summary>
///     Pipeline hop between <c>UseFunctionInvocation</c> and the provider-boundary budgeter that narrows the
///     <c>tools</c> array for the PROVIDER CALL ONLY: above the configured threshold the model is shown an always-on
///     core plus a relevance-ranked fill, and recovers the rest by calling <c>list_tools</c>.
///     <para>
///         Placing the filter here rather than in the offer projection is what makes the byte-identical default
///         structural. The offer, the runtime package, its config hash, the tighten-only approval wrap and the
///         <c>AllowedToolNames</c> intersection are literally unchanged code paths; only the options instance handed
///         downstream is narrowed. <c>FunctionInvokingChatClient</c> above keeps the WHOLE executable list, so a
///         revealed tool is immediately callable with its wrapper intact.
///     </para>
///     <para>
///         <b>Hidden is not forbidden.</b> The filter is a context-budget optimisation, never an authorisation
///         boundary. A hidden tool is one the model was not shown; if the model names it anyway it executes under
///         exactly today's rules — same wrapper, same policy — and an unresolvable name simply yields a not-found
///         result and the loop continues. Hiding never widens the authorised set and never waives an approval.
///     </para>
///     <para>
///         The hop refuses to filter any array that does not already carry a <see cref="ListToolsFunction" />
///         instance. Only the single-agent factory appends one, so orchestration participants and spawned sub-agents
///         are inert BY CONSTRUCTION rather than by heuristic — which is what makes "a hidden tool with no escape
///         hatch" unreachable.
///     </para>
/// </summary>
internal sealed class ToolRelevanceChatClient : DelegatingChatClient
{
    // The three MAF skill-discovery tools reach the model through AIContextProviders rather than the offer, so they
    // are named here the way ToolApprovalCoordinator names them. Always core: a skills agent that cannot see
    // load_skill cannot use its skills at all.
#pragma warning disable MAAI001 // Agent Skills is [Experimental] in Microsoft.Agents.AI; the same scoped suppression the provider call sites use.
    /// <summary>
    ///     The MAF skill tools, in one place. <c>InvocationAgentFactory</c> counts them by <c>Length</c> rather than
    ///     keeping its own constant, so a package bump that adds a fourth is a one-line edit here and cannot leave the
    ///     factory's threshold count and this core list disagreeing.
    /// </summary>
    internal static readonly string[] SkillToolNames =
    [
        AgentSkillsProvider.LoadSkillToolName,
        AgentSkillsProvider.ReadSkillResourceToolName,
        AgentSkillsProvider.RunSkillScriptToolName
    ];
#pragma warning restore MAAI001

    private readonly ILogger<ToolRelevanceChatClient> _logger;
    private readonly ToolRelevanceOptions _options;
    private readonly IToolRelevanceSelector _selector;

    public ToolRelevanceChatClient(IChatClient innerClient,
        IToolRelevanceSelector selector,
        ToolRelevanceOptions options,
        ILogger<ToolRelevanceChatClient> logger)
        : base(innerClient)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The shipped default (no scope, or an inactive one) is a straight delegation: the caller's own message
        // sequence and options instance go downstream, so the disabled path allocates nothing at all.
        if (ToolRelevanceScope.Current is not { Active: true })
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var resolved = await ResolveOptionsAsync(materialized, options, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(materialized, resolved, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (ToolRelevanceScope.Current is not { Active: true })
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var resolved = await ResolveOptionsAsync(materialized, options, cancellationToken).ConfigureAwait(false);

        await foreach (var update in base.GetStreamingResponseAsync(materialized, resolved, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    ///     Returns the options to send. The caller's own instance is returned unchanged — reference-equal, so no clone
    ///     and no reordering can occur — on every path that does not filter: no ambient scope, an inactive scope, no
    ///     tools, no <see cref="ListToolsFunction" /> in the array, a count at or below the threshold, or a blank
    ///     query. Otherwise a clone carrying the narrowed array is returned; the clone is seen only by the budgeter and
    ///     the provider, never by anything that dispatches a call.
    /// </summary>
    private async ValueTask<ChatOptions?> ResolveOptionsAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        if (ToolRelevanceScope.Current is not { Active: true } scope
            || options?.Tools is not { Count: > 0 } tools)
        {
            return options;
        }

        // Located by TYPE, not by name: the hop needs this exact instance for the Bind step anyway, so one pass both
        // gates and binds — and a foreign tool that merely takes the name cannot switch the filter on.
        if (tools.OfType<ListToolsFunction>().FirstOrDefault() is not { } listTools || tools.Count <= _options.Threshold)
        {
            return options;
        }

        var names = new string[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            names[index] = tools[index].Name;
        }

        var key = new ArrayKey(names);

        // ONE decision per array per turn, so the query matters only for a FIRST-time computation. An approval-resume
        // send replays the history plus one user message whose only content is ToolApprovalResponseContent - no text -
        // and re-deriving a query per send would resolve blank there, fall through to the full array mid-turn and
        // strand list_tools on the previous round's binding. Once this array HAS a decision it is reused whatever the
        // round's text looks like; only the no-decision-yet case still needs a query and can still pass through.
        var query = LastUserText(messages);
        if (string.IsNullOrWhiteSpace(query) && !scope.HasDecision(key))
        {
            return options;
        }

        ArrayDecision decision;
        try
        {
            decision = await scope.GetOrComputeAsync(key,
                                       () => SelectAsync(scope, tools, query, options, messages),
                                       cancellationToken)
                                   .ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An optimisation must never be able to fail a turn. IToolRelevanceSelector is a PUBLIC interface, so a
            // node-side or future selector can throw anything at all; whatever it is costs the saving and nothing
            // else, and the caller's own options instance goes downstream byte-identical. Counts only in the log:
            // no tool name, description or query text (trajectory policy). A cancel of the CALLER's own token is not
            // caught - the send really is going away.
            //
            // The exception OBJECT is deliberately not passed to the sink: sinks render Message and every inner
            // exception, and a selector failure carries the query and the tool descriptions into the failing call, so
            // an HTTP or provider error that echoes its request body would write raw trajectory content to disk under
            // an adjacent template that was scrubbed to counts. The TYPE name is the whole allow-listed diagnosis.
            _logger.LogWarning("Tool-relevance selection failed for an array of {ToolCount} tools ({FailureType}); sending the unfiltered offer.",
                tools.Count,
                exception.GetType().Name);
            return options;
        }

        // Bind the object FunctionInvokingChatClient itself resolves against — the one in the INCOMING array, not a
        // substitute in the clone below, which nothing would ever invoke.
        listTools.Bind(decision);

        // Offered union revealed, in the INPUT order, so a fixed set always serialises to the same tools array — a
        // stable prompt prefix and one GBNF compilation across the turn's rounds.
        var offered = new HashSet<string>(decision.OfferedNames, StringComparer.Ordinal);
        var narrowed = options.Clone();
        narrowed.Tools = [.. tools.Where(tool => offered.Contains(tool.Name) || decision.IsRevealed(tool.Name))];
        return narrowed;
    }

    /// <summary>
    ///     Computes one array's decision. <paramref name="query" /> is non-blank on every path that reaches here in
    ///     practice; it is nullable only for the narrow race where the array's entry is evicted between the
    ///     has-a-decision check and the single-flight publication, and <see cref="IToolRelevanceSelector" /> defines a
    ///     blank query as "no signal" rather than an error. Runs at most once per array per turn and — deliberately —
    ///     under NO caller token: the shared result must not be cancellable by whichever caller happened to arrive first. Its only
    ///     bound is the one the selector applies to itself.
    /// </summary>
    private async Task<ArrayDecision> SelectAsync(ToolRelevanceState scope,
        IList<AITool> tools,
        string? query,
        ChatOptions options,
        IReadOnlyList<ChatMessage> messages)
    {
        var instructionText = options.Instructions ?? FirstSystemText(messages);

        var candidates = new List<ToolRelevanceCandidate>(tools.Count);
        foreach (var tool in tools)
        {
            candidates.Add(new ToolRelevanceCandidate(tool.Name, tool.Description, IsCore(scope, tool.Name, instructionText)));
        }

        var selection = await _selector.SelectAsync(query, candidates, _options.Threshold, CancellationToken.None).ConfigureAwait(false);

        // Written from INSIDE the single-flight factory, so a racing second caller on the same array awaits the same
        // task and writes nothing: the counts are per ARRAY, not per round. EXCHANGED rather than added, so the turn's
        // notice reports the counts of one real decision. A turn that rebinds mid-turn (a changed array shape, §3.4a)
        // therefore reports the array the model ended on rather than a sum that double-counts the tools both arrays
        // held — a sum would overstate the "of M" the notice claims.
        _ = Interlocked.Exchange(ref scope.PendingNoticeHiddenCount, selection.HiddenNames.Count);
        _ = Interlocked.Exchange(ref scope.PendingNoticeTotalCount, candidates.Count);

        return new ArrayDecision
        {
            OfferedNames = selection.OfferedNames,
            HiddenNames = selection.HiddenNames
        };
    }

    // Tool AUTHORISATION is never an input here. The core set is a fixed node-wide name set (work-session tools plus
    // approval-bearing built-ins), joined by the names this assembly owns and by any tool the agent's own instructions
    // name verbatim. MCP and custom tools are deliberately absent and are ranked like everything else.
    private static bool IsCore(ToolRelevanceState scope, string name, string? instructionText)
    {
        if (scope.CoreNames.Contains(name)
            || string.Equals(name, AskUserTool.ToolName, StringComparison.Ordinal)
            || string.Equals(name, ListToolsFunction.ToolName, StringComparison.Ordinal)
            || SkillToolNames.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        // A WORD-boundary match, not a bare substring test: a short built-in name over-pinned on any instruction that
        // merely contained it inside a longer word ("ask" inside "task"), which spent the saving on a tool the
        // instructions never named. Boundary is the \w class - letters, digits, and the underscore snake_case names
        // use - so "ask" matches "you may ask first" and neither "task" nor "ask_user". The trade is deliberate and
        // it does cut both ways: an instruction that names a tool only INSIDE a longer token no longer pins it, so
        // that tool drops back to being a trimmable candidate ranked like any other.
        return instructionText is not null && ContainsWord(instructionText, name);
    }

    /// <summary>
    ///     Ordinal word-boundary containment. A hand-rolled scan rather than a regex because the pattern is the TOOL
    ///     NAME: every candidate on every array decision would build and discard its own compiled <c>Regex</c>.
    /// </summary>
    private static bool ContainsWord(string text, string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return false;
        }

        for (var index = text.IndexOf(word, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(word, index + 1, StringComparison.Ordinal))
        {
            if (!IsWordCharacter(text, index - 1) && !IsWordCharacter(text, index + word.Length))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordCharacter(string text, int index) =>
        index >= 0 && index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_');

    // The relevance query. Instructions are null on both ROOT agent-build paths by design (the system prompt rides the
    // seed message), so the query is derived from the round's messages the hop already receives. A text-LESS user
    // message is skipped rather than accepted as a blank query: the runner appends approval responses as a user-role
    // message carrying only ToolApprovalResponseContent, and the round's real question is the text-bearing user
    // message behind it.
    private static string? LastUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.User && !string.IsNullOrWhiteSpace(messages[index].Text))
            {
                return messages[index].Text;
            }
        }

        return null;
    }

    private static string? FirstSystemText(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                return message.Text;
            }
        }

        return null;
    }
}
