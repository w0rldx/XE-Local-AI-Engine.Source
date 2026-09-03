namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    private static readonly string[] SkillToolNames =
    [
        AgentSkillsProvider.LoadSkillToolName,
        AgentSkillsProvider.ReadSkillResourceToolName,
        AgentSkillsProvider.RunSkillScriptToolName
    ];
#pragma warning restore MAAI001

    private readonly ToolRelevanceOptions _options;
    private readonly IToolRelevanceSelector _selector;

    public ToolRelevanceChatClient(IChatClient innerClient, IToolRelevanceSelector selector, ToolRelevanceOptions options)
        : base(innerClient)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var resolved = await ResolveOptionsAsync(materialized, options, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(materialized, resolved, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
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

        var query = LastUserText(messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            return options;
        }

        var names = new string[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            names[index] = tools[index].Name;
        }

        var key = new ArrayKey(names);
        var decision = await scope.GetOrComputeAsync(key,
                                      () => SelectAsync(scope, tools, query, options, messages),
                                      cancellationToken)
                                  .ConfigureAwait(false);

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
    ///     Computes one array's decision. Runs at most once per array per turn and — deliberately — under NO caller
    ///     token: the shared result must not be cancellable by whichever caller happened to arrive first. Its only
    ///     bound is the one the selector applies to itself.
    /// </summary>
    private async Task<ArrayDecision> SelectAsync(ToolRelevanceState scope,
        IList<AITool> tools,
        string query,
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

        // Added from INSIDE the single-flight factory, so a racing second caller on the same array awaits the same task
        // and adds nothing: the counts are per array, not per round.
        _ = Interlocked.Add(ref scope.PendingNoticeHiddenCount, selection.HiddenNames.Count);
        _ = Interlocked.Add(ref scope.PendingNoticeTotalCount, candidates.Count);

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

        return instructionText is not null && instructionText.Contains(name, StringComparison.Ordinal);
    }

    // The relevance query. Instructions are null on both ROOT agent-build paths by design (the system prompt rides the
    // seed message), so the query is derived from the round's messages the hop already receives.
    private static string? LastUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.User)
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
