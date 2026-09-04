namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

/// <summary>
///     Whether the agent a work session is bound to would really be offered a tool that writes files or runs commands —
///     <c>GRAPH-C4-2</c>'s runtime half, asked in ONE place so the two callers that ask it cannot answer differently.
///     <para>
///         Its only armed caller is the development-workflow lane, which is why the refusal is worded in that lane's
///         vocabulary: the node's declaration and the template's waiver are the two things an operator can actually
///         change, and a sentence about a work session would send them to a screen with no such setting on it. Nothing
///         arms it for ordinary chat, an agent-home session or a saved-agent run, so nothing about those moves.
///     </para>
///     <para>
///         The question is asked of the RESOLVER's own answer and never of a re-derived <c>offer ∩ allowedToolNames</c>.
///         The seeded Default Assistant takes the WHOLE capability-gated offer while shipping an empty allowed set, so
///         a re-derived intersection is empty for exactly the binding whose reach is widest. A binding the resolver
///         cannot answer for is judged on the offer the DEFAULT PERSONA would then be handed, because that is what
///         the turn really runs on — the one place the offer is read directly, and read whole rather than narrowed.
///     </para>
///     <para>
///         This instance half is the EARLY answer only — the one the development-workflow lane asks BEFORE a session
///         exists, where a refusal costs nothing and reads as configuration. It must never be the enforcing one: it
///         resolves the definition itself, and the turn it precedes resolves that same mutable definition again, so a
///         widening that lands between the two reaches the send. The enforcing answer is <see cref="Refuse" />, asked
///         by the send path of the ONE projection it is about to hand the model — one resolution, one decision.
///     </para>
///     <para>
///         ponytail: this runs a COMPLETE <see cref="IAgentDefinitionResolver.ResolveAsync" /> — persona composition and
///         the playbook read included — to read tool categories. Every read is a store read on a cache-first path and
///         there is no provider round trip (the retrieval query is null, so the playbook takes its static prepend). The
///         upgrade path is a projection-only overload on that resolver returning <c>AllowedTools</c> alone; worth it
///         only if a profile ever shows this resolve mattering against the dispatch it guards.
///     </para>
/// </summary>
internal sealed class WorkSessionWriteDeclarationGuard
{
    /// <summary>
    ///     The four work-session state tools, by name. They are <c>WriteExecute</c> because they write durable session
    ///     rows, which is the only write category the enum has — and they are what EVERY workflow agent node is offered,
    ///     so counting them would refuse every agent node there is. Read off the catalog rather than listed here, so a
    ///     fifth state tool joins the exclusion by being declared, and a new write tool that is NOT one of them counts
    ///     until someone adds it there.
    /// </summary>
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
    /// <param name="agentDefinitionId">The definition the turn will resolve — the session's own binding, not the node's.</param>
    /// <param name="pinnedModelOverride">
    ///     The caller's model pin, which wins over the definition's exactly as it does at dispatch. The offer is
    ///     capability-gated, so the effective model is resolved first: a null model returns a THINNER offer and the
    ///     check would under-block on precisely the runs a later model swap turns into writes.
    /// </param>
    public async Task<string?> InspectAsync(Guid agentDefinitionId, string? pinnedModelOverride, CancellationToken cancellationToken)
    {
        var activeModel = string.IsNullOrWhiteSpace(pinnedModelOverride)
            ? await _localDefaultModel.ResolveAsync((await _nodeSettings.LoadAsync(cancellationToken).ConfigureAwait(false)).DefaultModelName, cancellationToken)
                .ConfigureAwait(false)
            : pinnedModelOverride;

        // supportsTools: true is passed deliberately rather than probed. The question is what this binding COULD be
        // offered, and a probe answering false would make the check inert exactly where it is needed.
        var resolved = await _runtimes.ResolveAsync(agentDefinitionId,
                                          activeModel,
                                          retrievalQuery: null,
                                          supportsTools: true,
                                          honorModelProfile: string.IsNullOrWhiteSpace(pinnedModelOverride),
                                          activeModelIsCloud: false,
                                          cancellationToken)
                                      .ConfigureAwait(false);
        // A binding that resolves to nothing is not a case with no answer: the turn keeps the DEFAULT PERSONA, which
        // takes the whole capability-gated offer, so the honest question is what THAT offer carries. Judging the
        // fallback rather than assuming the worst is what keeps the rule quiet on a node whose fallback is offered
        // nothing that writes, and blocking on one whose fallback is offered everything — which is the real bypass:
        // delete the definition mid-session and a rule that only ever judged resolved bindings would go silent.
        var projection = resolved?.AllowedTools
                         ?? await _offer.GetOfferedToolsAsync(activeModel, isCloudModel: false, cancellationToken).ConfigureAwait(false);
        return Refuse(projection, bindingResolved: resolved is not null);
    }

    /// <summary>
    ///     The refusal a turn's OWN tool offer earns, or <see langword="null" /> when it carries no undeclared write.
    ///     <para>
    ///         The enforcing half of <c>GRAPH-C4-2</c>, and the reason it takes the offer rather than an id: the send
    ///         path calls it with the very list it is about to put in the runtime package, so there is nothing between
    ///         the decision and the send for an operator to edit. A turn that is offered no tools at all — the engine
    ///         switched off, or a model that cannot call them — carries no write and is not refused.
    ///     </para>
    /// </summary>
    /// <param name="offer">The tools this turn will really be handed, or <see langword="null" /> when it offers none.</param>
    /// <param name="bindingResolved">
    ///     Whether the turn resolved the session's own agent definition. False means it fell back to the default
    ///     persona — the definition was deleted or never bound — which is a different sentence for the operator,
    ///     because the fix is to restore the agent rather than to narrow it.
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
///     <para>
///         Thrown out of the send path rather than streamed as a terminal so it cannot be mistaken for a provider
///         failure: the work-session supervisor catches it, records the gate that stopped the step, and settles the
///         session with this message — which the owning run then blocks its node run with, under the <c>Policy</c>
///         failure class.
///     </para>
/// </summary>
internal sealed class WorkSessionUndeclaredWriteException(string message) : InvalidOperationException(message);
