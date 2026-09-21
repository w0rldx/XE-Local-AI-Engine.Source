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
///     <c>tools</c> array for the PROVIDER CALL ONLY, to an always-on core plus a relevance-ranked fill.
/// </summary>
/// <remarks>
///     Engages only above the configured threshold; the model recovers what is held back by calling <c>list_tools</c>.
///     <b>Hidden is not forbidden</b>: a context budget, never an authorisation boundary — a tool the model was not
///     shown still executes under today's wrapper and policy. The hop refuses to filter an array with no
///     <see cref="ListToolsFunction" /> instance, so "a hidden tool with no escape hatch" is unreachable.
///     See docs/wiki/04-agent-mode.md ("Tool-relevance narrowing").
/// </remarks>
internal sealed class ToolRelevanceChatClient : DelegatingChatClient
{
    // The three MAF skill-discovery tools reach the model through AIContextProviders rather than the offer, so they are
    // named here the way ToolApprovalCoordinator names them; always core, or a skills agent cannot use its skills.
#pragma warning disable MAAI001 // Agent Skills is [Experimental] in Microsoft.Agents.AI; the same scoped suppression the provider call sites use.
    /// <summary>
    ///     The MAF skill tools, in one place; <c>InvocationAgentFactory</c> counts them by <c>Length</c> rather than
    ///     keeping its own constant, so its threshold count cannot drift from this core list.
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
    ///     Returns the options to send: the caller's own instance, reference-equal, on every path that does not filter,
    ///     otherwise a clone carrying the narrowed array.
    /// </summary>
    /// <remarks>
    ///     Non-filtering paths are no ambient scope, an inactive scope, no tools, no <see cref="ListToolsFunction" /> in
    ///     the array, a count at or below the threshold, or a blank query — reference equality means no clone and no
    ///     reordering can occur there. The clone is seen only by the budgeter and the provider, never by anything that
    ///     dispatches a call.
    /// </remarks>
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

        // ONE decision per array per turn, so the query matters only for a FIRST-time computation: a text-less
        // approval-resume send must not resolve blank, fall through mid-turn and strand list_tools on the old binding.
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
            // An optimisation must never fail a turn, and the log carries counts plus the TYPE name only: the exception
            // OBJECT would drag the query and tool descriptions into a sink that renders every inner exception.
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
    ///     Computes one array's decision, at most once per array per turn.
    /// </summary>
    /// <remarks>
    ///     <paramref name="query" /> is non-blank on every path that reaches here in practice; it is nullable only for
    ///     the narrow race where the array's entry is evicted between the has-a-decision check and the single-flight
    ///     publication, and <see cref="IToolRelevanceSelector" /> defines a blank query as "no signal" rather than an
    ///     error. Runs deliberately under NO caller token — the shared result must not be cancellable by whichever
    ///     caller arrived first — so its only bound is the one the selector applies to itself.
    /// </remarks>
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
            candidates.Add(new ToolRelevanceCandidate { Name = tool.Name, Description = tool.Description, IsCore = IsCore(scope, tool.Name, instructionText) });
        }

        var selection = await _selector.SelectAsync(query, candidates, _options.Threshold, CancellationToken.None).ConfigureAwait(false);

        // Counts are per ARRAY, not per round: written from INSIDE the single-flight factory and EXCHANGED rather than
        // added, so a turn that rebinds reports the array the model ended on, not a sum overstating the notice's "of M".
        _ = Interlocked.Exchange(ref scope.PendingNoticeHiddenCount, selection.HiddenNames.Count);
        _ = Interlocked.Exchange(ref scope.PendingNoticeTotalCount, candidates.Count);

        return new ArrayDecision
        {
            OfferedNames = selection.OfferedNames,
            HiddenNames = selection.HiddenNames
        };
    }

    // Tool AUTHORISATION is never an input here: the core set is a fixed node-wide name set plus the names this
    // assembly owns plus what the instructions name. MCP and custom tools are absent and rank like everything else.
    private static bool IsCore(ToolRelevanceState scope, string name, string? instructionText)
    {
        if (scope.CoreNames.Contains(name)
            || string.Equals(name, AskUserTool.ToolName, StringComparison.Ordinal)
            || string.Equals(name, ListToolsFunction.ToolName, StringComparison.Ordinal)
            || SkillToolNames.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        // A WORD-boundary match on the \w class, not a bare substring test, which over-pinned a short name on any
        // instruction merely containing it ("ask" inside "task"). It cuts both ways, deliberately.
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

    // The relevance query. Instructions are null on both ROOT agent-build paths by design, so it comes from the round's
    // messages; a text-LESS user message is skipped because an approval response carries no text behind it.
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
