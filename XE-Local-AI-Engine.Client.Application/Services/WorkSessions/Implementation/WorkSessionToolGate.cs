namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     What an agent's effective model is, and whether the tool gates a work session depends on admit it.
/// </summary>
/// <remarks>
///     <see cref="SupportsTools" /> is the model's own capability, detected from its chat template or the declared
///     cloud matrix; <see cref="IsAllowListed" /> is the operator's tool-capable allow-list. They come from different
///     sources and are free to disagree, which is why both are reported. <see cref="SupportsTools" /> is
///     <see langword="null" /> when the probe was NOT run (<see cref="WorkSessionToolGate.InspectAllowListAsync" />):
///     "not asked" and "asked, and the model cannot" are different facts.
/// </remarks>
internal readonly record struct WorkSessionToolGateVerdict(
    bool AgentExists,
    string AgentName,
    string? EffectiveModel,
    bool? SupportsTools,
    bool IsAllowListed,
    bool ModelIsCallerPinned = false)
{
    /// <summary>How a refusal names the model, which has to name the thing the operator would go and change.</summary>
    /// <remarks>
    ///     A caller pin — a development-workflow node's <c>modelProfile</c> — is not something the agent "runs on": the
    ///     agent may be pinned to something else, and the agent's settings would be the wrong screen.
    /// </remarks>
    public string Subject =>
        ModelIsCallerPinned
            ? $"This work session is pinned to '{EffectiveModel}'"
            : $"'{AgentName}' runs on '{EffectiveModel}'";

    /// <summary>Where the model that was refused can be changed, which follows from the same distinction.</summary>
    public string Remedy =>
        ModelIsCallerPinned
            ? "pin a listed model instead"
            : "pick an agent on a listed model";
}

/// <summary>
///     Answers, for one agent definition, whether a work session could actually call its four state tools.
/// </summary>
/// <remarks>
///     Two independent gates decide it: the model capability probe and the operator's
///     <c>AgentHome:ToolCapableModels</c> allow-list, which the offer applies unconditionally, cloud pins included.
///     ONE seam, so create, repoint and the step loop judge a session identically, and the allow-list is asked of
///     <see cref="ILocalToolOfferProvider.IsToolCapable" /> — the method the offer itself applies — so the two cannot
///     drift. What a silent miss costs: docs/wiki/04-agent-mode.md ("what a repoint may not do").
/// </remarks>
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
    ///     The refusal an operator can act on: it names the model that was rejected and where the list that rejected
    ///     it lives.
    /// </summary>
    /// <remarks>
    ///     Deliberately distinct from the capability refusal — the two failures have different fixes, and blaming the
    ///     model would send the operator hunting a different agent when one line in Node Settings is what they need.
    /// </remarks>
    public static string AllowListRefusal(WorkSessionToolGateVerdict verdict) =>
        $"{verdict.Subject}, which is not in this node's tool-capable model list (Node Settings → Tools), so work-session tools such as update_work_plan would silently be withheld. "
        + $"Add the model to the list, or {verdict.Remedy}.";

    /// <summary>
    ///     Resolves the agent and reports BOTH gates against the model the session would run on: the caller's own pin,
    ///     else the agent's, else the node's default chat model.
    /// </summary>
    /// <remarks>
    ///     For the create and repoint boundary, which has to tell the two failures apart to say which one the operator
    ///     should fix. A missing definition answers <c>AgentExists: false</c> rather than throwing: the create path
    ///     turns that into its own refusal, while the step loop must not stop a step over an agent deleted under it.
    /// </remarks>
    public async Task<WorkSessionToolGateVerdict> InspectAsync(Guid agentDefinitionId, string? pinnedModelOverride, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(agentDefinitionId, pinnedModelOverride, cancellationToken);
        if (resolved.EffectiveModel is not { } effectiveModel)
        {
            return resolved;
        }

        // AFTER the model resolves and only here: this can be an /api/show round-trip on a cold cache, whereas the
        // allow-list question is a memory-cache read the step loop takes through InspectAllowListAsync for free.
        var capabilities = await _capabilityResolver.ResolveAsync(effectiveModel, cancellationToken);
        return resolved with
        {
            SupportsTools = capabilities.SupportsTools
        };
    }

    /// <summary>
    ///     The allow-list gate ALONE — same agent and effective-model resolution, no capability probe, so
    ///     <see cref="WorkSessionToolGateVerdict.SupportsTools" /> comes back <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     For the step loop, which reads only <c>IsAllowListed</c>. The probe would be a provider round-trip on the
    ///     node's one invocation slot for an answer nobody reads, and one more thing that can throw inside a loop
    ///     whose failure mode is terminalizing the session.
    /// </remarks>
    public async Task<WorkSessionToolGateVerdict> InspectAllowListAsync(Guid agentDefinitionId, string? pinnedModelOverride, CancellationToken cancellationToken) =>
        await ResolveAsync(agentDefinitionId, pinnedModelOverride, cancellationToken);

    /// <summary>
    ///     The half both entry points share: the agent, its effective model, and the allow-list answer. Reports
    ///     <c>SupportsTools: null</c> — the probe is the caller's decision.
    /// </summary>
    private async Task<WorkSessionToolGateVerdict> ResolveAsync(Guid agentDefinitionId, string? pinnedModelOverride, CancellationToken cancellationToken)
    {
        var definition = await _agentDefinitionStore.GetByIdAsync(agentDefinitionId, cancellationToken);
        if (definition is null)
        {
            return new WorkSessionToolGateVerdict(AgentExists: false, string.Empty, EffectiveModel: null, SupportsTools: null, IsAllowListed: false);
        }

        // The caller's pin first, then the definition's, then the node default. Same order the model itself is applied
        // in, so what this gate judges is always the model the session's turns will actually run on.
        var effectiveModel = await ResolveEffectiveModelAsync(Pin(pinnedModelOverride) ?? definition.ModelProfile, cancellationToken);
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            return new WorkSessionToolGateVerdict(AgentExists: true, definition.Name, EffectiveModel: null, SupportsTools: null, IsAllowListed: false);
        }

        return new WorkSessionToolGateVerdict(AgentExists: true,
            definition.Name,
            effectiveModel,
            SupportsTools: null,
            _toolOffer.IsToolCapable(effectiveModel),
            ModelIsCallerPinned: Pin(pinnedModelOverride) is not null);
    }

    private static string? Pin(string? candidate) =>
        string.IsNullOrWhiteSpace(candidate) ? null : candidate;

    private async Task<string?> ResolveEffectiveModelAsync(string? pinnedModel, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(pinnedModel))
        {
            return pinnedModel;
        }

        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);
        return await _defaultModelResolver.ResolveAsync(nodeSettings.DefaultModelName, cancellationToken);
    }
}
