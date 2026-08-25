namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     What an agent's effective model is, and whether the tool gates a work session depends on admit it.
///     <para>
///         <see cref="SupportsTools" /> is the model's own capability, detected from its chat template (or the declared
///         cloud matrix). <see cref="IsAllowListed" /> is the operator's tool-capable allow-list. They come from
///         different sources and are free to disagree, which is the whole reason both are reported.
///     </para>
///     <para>
///         <see cref="SupportsTools" /> is <see langword="null" /> when the capability probe was NOT run — the answer
///         from <see cref="WorkSessionToolGate.InspectAllowListAsync" />, which a caller that only needs the allow-list
///         takes so it does not pay for a provider round-trip. "Not asked" and "asked, and the model cannot" are
///         different facts, so they are different values rather than a shared <see langword="false" />.
///     </para>
/// </summary>
internal readonly record struct WorkSessionToolGateVerdict(
    bool AgentExists,
    string AgentName,
    string? EffectiveModel,
    bool? SupportsTools,
    bool IsAllowListed);

/// <summary>
///     Answers, for one agent definition, whether a work session could actually call its four state tools.
///     <para>
///         A work session is worthless without <c>update_work_plan</c> and <c>complete_work_session</c>: with them
///         withheld the model's calls come back "Requested function … not found" and the session burns its whole step
///         budget writing nothing. Two independent gates decide that, and checking only one is what made the failure
///         silent — the model capability probe (template / <c>/api/show</c> / declared cloud matrix) and the operator's
///         <c>AgentHome:ToolCapableModels</c> allow-list, which the offer applies unconditionally, cloud pins included.
///     </para>
///     <para>
///         This is one seam rather than two copies on purpose: the create path, the repoint path and the step loop must
///         judge the same session identically, and the allow-list predicate is asked of
///         <see cref="ILocalToolOfferProvider.IsToolCapable" /> — the very method the offer applies — so the two can
///         never drift.
///     </para>
///     <para>
///         Two entry points, because the two callers need different halves and the halves cost different amounts.
///         <see cref="InspectAsync" /> answers both gates for the create/repoint boundary and pays for the capability
///         probe. <see cref="InspectAllowListAsync" /> answers the allow-list alone — a memory-cache read — for the
///         step loop, which reads nothing else.
///     </para>
/// </summary>
internal sealed class WorkSessionToolGate
{
    private readonly IAgentDefinitionStore _agentDefinitionStore;
    private readonly IModelCapabilityResolver _capabilityResolver;
    private readonly ILocalDefaultChatModelResolver _defaultModelResolver;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly ILocalToolOfferProvider _toolOffer;

    public WorkSessionToolGate(IAgentDefinitionStore agentDefinitionStore,
        IModelCapabilityResolver capabilityResolver,
        ILocalDefaultChatModelResolver defaultModelResolver,
        INodeSettingsStore nodeSettingsStore,
        ILocalToolOfferProvider toolOffer)
    {
        _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
        _capabilityResolver = capabilityResolver ?? throw new ArgumentNullException(nameof(capabilityResolver));
        _defaultModelResolver = defaultModelResolver ?? throw new ArgumentNullException(nameof(defaultModelResolver));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _toolOffer = toolOffer ?? throw new ArgumentNullException(nameof(toolOffer));
    }

    /// <summary>
    ///     The refusal an operator can act on: it names the model that was rejected and where the list that rejected it
    ///     lives. Deliberately distinct from the capability refusal — the two failures have different fixes, and a
    ///     message that blamed the model would send the operator looking for a different agent when adding one line to
    ///     Node Settings is what they need.
    /// </summary>
    public static string AllowListRefusal(string agentName, string? effectiveModel) =>
        $"'{agentName}' runs on '{effectiveModel}', which is not in this node's tool-capable model list (Node Settings → Tools), so work-session tools such as update_work_plan would silently be withheld. Add the model to the list, or pick an agent on a listed model.";

    /// <summary>
    ///     Resolves the agent and reports BOTH gates against the model the session would actually run on: the agent's
    ///     pin, or the node's default chat model when it has none. For the create and repoint boundary, which has to
    ///     tell the two failures apart to say which one the operator should fix.
    ///     <para>
    ///         A missing definition answers <c>AgentExists: false</c> rather than throwing — the create path turns that
    ///         into its own refusal, while the step loop has nothing to say about a session whose agent was deleted
    ///         underneath it and must not stop the step for it.
    ///     </para>
    /// </summary>
    public async Task<WorkSessionToolGateVerdict> InspectAsync(Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (resolved.EffectiveModel is not { } effectiveModel)
        {
            return resolved;
        }

        // Deliberately AFTER the model resolves and only here: this can be an Ollama /api/show round-trip on a cold
        // cache. The allow-list question below is a memory-cache read, so the caller that needs only that one takes
        // InspectAllowListAsync instead and pays nothing.
        var capabilities = await _capabilityResolver.ResolveAsync(effectiveModel, cancellationToken).ConfigureAwait(false);
        return resolved with
        {
            SupportsTools = capabilities.SupportsTools
        };
    }

    /// <summary>
    ///     The allow-list gate ALONE — same agent and effective-model resolution, no capability probe, so
    ///     <see cref="WorkSessionToolGateVerdict.SupportsTools" /> comes back <see langword="null" />.
    ///     <para>
    ///         This exists for the step loop. That guard reads only <c>IsAllowListed</c>, and the probe it would
    ///         otherwise trigger is a provider round-trip on the node's one invocation slot — cost for an answer nobody
    ///         reads, and one more thing that can throw inside a loop whose failure mode is terminalizing the session.
    ///     </para>
    /// </summary>
    public async Task<WorkSessionToolGateVerdict> InspectAllowListAsync(Guid agentDefinitionId, CancellationToken cancellationToken) =>
        await ResolveAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     The half both entry points share: the agent, its effective model, and the allow-list answer. Reports
    ///     <c>SupportsTools: null</c> — the probe is the caller's decision.
    /// </summary>
    private async Task<WorkSessionToolGateVerdict> ResolveAsync(Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var definition = await _agentDefinitionStore.GetByIdAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return new WorkSessionToolGateVerdict(AgentExists: false, string.Empty, EffectiveModel: null, SupportsTools: null, IsAllowListed: false);
        }

        var effectiveModel = await ResolveEffectiveModelAsync(definition.ModelProfile, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            return new WorkSessionToolGateVerdict(AgentExists: true, definition.Name, EffectiveModel: null, SupportsTools: null, IsAllowListed: false);
        }

        return new WorkSessionToolGateVerdict(AgentExists: true,
            definition.Name,
            effectiveModel,
            SupportsTools: null,
            _toolOffer.IsToolCapable(effectiveModel));
    }

    private async Task<string?> ResolveEffectiveModelAsync(string? pinnedModel, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(pinnedModel))
        {
            return pinnedModel;
        }

        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return await _defaultModelResolver.ResolveAsync(nodeSettings.DefaultModelName, cancellationToken).ConfigureAwait(false);
    }
}
