namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

/// <summary>
///     Whether the agent a work session is bound to would really be offered a tool that writes files or runs commands:
///     <c>GRAPH-C4-2</c>'s runtime half, asked in ONE place so the two callers cannot answer differently.
/// </summary>
/// <remarks>
///     Its only armed caller is the development-workflow lane, so the refusal speaks that lane's vocabulary — the
///     node's declaration and the template's waiver are what an operator can change. Nothing arms it for ordinary
///     chat, an agent-home session or a saved-agent run. This instance half is the EARLY answer only: it resolves the
///     definition, the turn resolves that same mutable definition again, and a widening between the two would reach
///     the send. <see cref="Refuse" /> is the enforcing answer — one resolution, one decision.
/// </remarks>
internal sealed class WorkSessionWriteDeclarationGuard
{
    /// <summary>The four work-session state tools, by name, excluded from the undeclared-write check.</summary>
    /// <remarks>
    ///     They are <c>WriteExecute</c> because they write durable session rows — the enum's only write category — and
    ///     every workflow agent node is offered them, so counting them would refuse every agent node there is. Read
    ///     off the catalog, so a fifth state tool joins the exclusion by being declared and any other write tool
    ///     counts until someone adds it there.
    /// </remarks>
    private static readonly HashSet<string> SessionRowTools = [.. WorkSessionToolCatalog.Descriptors.Select(static descriptor => descriptor.Name)];

    private readonly ILocalDefaultChatModelResolver _localDefaultModel;
    private readonly INodeSettingsStore _nodeSettings;
    private readonly ILocalToolOfferProvider _offer;
    private readonly IAgentDefinitionResolver _runtimes;

    public WorkSessionWriteDeclarationGuard(IAgentDefinitionResolver runtimes,
        ILocalToolOfferProvider offer,
        INodeSettingsStore nodeSettings,
        ILocalDefaultChatModelResolver localDefaultModel)
    {
        _runtimes = runtimes ?? throw new ArgumentNullException(nameof(runtimes));
        _offer = offer ?? throw new ArgumentNullException(nameof(offer));
        _nodeSettings = nodeSettings ?? throw new ArgumentNullException(nameof(nodeSettings));
        _localDefaultModel = localDefaultModel ?? throw new ArgumentNullException(nameof(localDefaultModel));
    }

    /// <summary>
    ///     The refusal this binding earns, or <see langword="null" /> when its offer carries no undeclared write.
    /// </summary>
    /// <remarks>
    ///     Asked of the RESOLVER's answer, never a re-derived <c>offer ∩ allowedToolNames</c>: the seeded Default
    ///     Assistant takes the whole capability-gated offer while shipping an empty allowed set, so an intersection is
    ///     empty for exactly the binding whose reach is widest. A binding that resolves to nothing is judged on the
    ///     offer the DEFAULT PERSONA would be handed, or deleting the definition mid-session becomes the real bypass.
    /// </remarks>
    /// <param name="agentDefinitionId">The definition the turn will resolve — the session's binding, not the node's.</param>
    /// <param name="pinnedModelOverride">
    ///     The caller's model pin, which wins as at dispatch. Resolved first: the offer is capability-gated, so a null
    ///     model thins it and the check under-blocks.
    /// </param>
    public async Task<string?> InspectAsync(Guid agentDefinitionId, string? pinnedModelOverride, CancellationToken cancellationToken)
    {
        var activeModel = string.IsNullOrWhiteSpace(pinnedModelOverride)
            ? await _localDefaultModel.ResolveAsync((await _nodeSettings.LoadAsync(cancellationToken)).DefaultModelName, cancellationToken)
            : pinnedModelOverride;

        // supportsTools: true rather than probed — a probe answering false makes the check inert where it is needed.
        // ponytail: a full IAgentDefinitionResolver.ResolveAsync for tool categories; narrow to AllowedTools if it profiles.
        var resolved = await _runtimes.ResolveAsync(agentDefinitionId,
                                          activeModel,
                                          retrievalQuery: null,
                                          supportsTools: true,
                                          honorModelProfile: string.IsNullOrWhiteSpace(pinnedModelOverride),
                                          activeModelIsCloud: false,
                                          cancellationToken);
        // An unresolved binding keeps the DEFAULT PERSONA and its whole capability-gated offer, so that offer is the
        // honest question — judging the fallback, not assuming the worst.
        var projection = resolved?.AllowedTools
                         ?? await _offer.GetOfferedToolsAsync(activeModel, isCloudModel: false, cancellationToken);
        return Refuse(projection, bindingResolved: resolved is not null);
    }

    /// <summary>
    ///     The refusal a turn's OWN tool offer earns, or <see langword="null" /> when it carries no undeclared write.
    /// </summary>
    /// <remarks>
    ///     The enforcing half of <c>GRAPH-C4-2</c>, which is why it takes the offer rather than an id: the send path
    ///     calls it with the very list it is about to put in the runtime package, leaving nothing between the decision
    ///     and the send for an operator to edit. A turn offered no tools at all carries no write and is not refused.
    /// </remarks>
    /// <param name="offer">The tools this turn will really be handed, or <see langword="null" /> when it offers none.</param>
    /// <param name="bindingResolved">
    ///     Whether the turn resolved the session's own agent definition; false means the default persona, whose fix is
    ///     to restore the agent, not narrow it.
    /// </param>
    public static string? Refuse(IReadOnlyList<AllowedToolDto>? offer, bool bindingResolved)
    {
        if (offer?.FirstOrDefault(static tool => tool.Category == ToolCategory.WriteExecute && !SessionRowTools.Contains(tool.Name)) is not { } write)
        {
            return null;
        }

        return bindingResolved
            ? $"This node is bound to an agent that will be offered '{write.Name}', which can write files or run commands outside this node's sandbox, and the node "
              + "declares no 'WriteExecute' capability. Declare it on the node — which then needs a human gate on every path into it — or set 'allowUngatedWrites' on "
              + "this template and say why (invariant GRAPH-C4-2)."
            : $"This node is bound to an agent definition that no longer exists, so its turn falls back to the default assistant — which is offered '{write.Name}', a "
              + "tool that can write files or run commands outside this node's sandbox — while the node declares no 'WriteExecute' capability. Restore the agent, bind "
              + "another, or set 'allowUngatedWrites' on this template and say why (invariant GRAPH-C4-2).";
    }
}

/// <summary>
///     A turn refused before it was sent because its own tool offer carried a write/execute tool the development-workflow
///     node driving it never declared (<c>GRAPH-C4-2</c>).
/// </summary>
/// <remarks>
///     Thrown out of the send path rather than streamed as a terminal, so it cannot be mistaken for a provider
///     failure: the supervisor catches it, records the gate that stopped the step, and settles the session with this
///     message, which the owning run then blocks its node run with under the <c>Policy</c> failure class.
/// </remarks>
internal sealed class WorkSessionUndeclaredWriteException : InvalidOperationException
{
    public WorkSessionUndeclaredWriteException(string message) : base(message)
    {
    }
}
