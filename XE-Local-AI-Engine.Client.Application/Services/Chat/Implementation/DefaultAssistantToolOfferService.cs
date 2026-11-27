namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Reads the tool offer a chat turn hands the seeded Default Assistant, so the agent form shows the server's list
///     instead of guessing it.
/// </summary>
/// <remarks>
///     It runs the send path's model head, turn resolution and tool-offer gate, stopped before anything is persisted or
///     run.
/// </remarks>
public sealed class DefaultAssistantToolOfferService
{
    private readonly IDefaultAgentProvider _defaultAgentProvider;
    private readonly ILocalDefaultChatModelResolver _localDefaultChatModelResolver;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IToolApprovalPolicy _toolApprovalPolicy;
    private readonly ChatTurnResolver _turnResolver;

    public DefaultAssistantToolOfferService(ChatTurnResolver turnResolver,
        IDefaultAgentProvider defaultAgentProvider,
        INodeSettingsStore nodeSettingsStore,
        ILocalDefaultChatModelResolver localDefaultChatModelResolver,
        INodeRuntimeSettings runtimeSettings,
        ILocalToolOfferProvider localToolOfferProvider,
        IToolApprovalPolicy toolApprovalPolicy)
    {
        ArgumentNullException.ThrowIfNull(turnResolver);
        _turnResolver = turnResolver;
        ArgumentNullException.ThrowIfNull(defaultAgentProvider);
        _defaultAgentProvider = defaultAgentProvider;
        ArgumentNullException.ThrowIfNull(nodeSettingsStore);
        _nodeSettingsStore = nodeSettingsStore;
        ArgumentNullException.ThrowIfNull(localDefaultChatModelResolver);
        _localDefaultChatModelResolver = localDefaultChatModelResolver;
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _runtimeSettings = runtimeSettings;
        ArgumentNullException.ThrowIfNull(localToolOfferProvider);
        _localToolOfferProvider = localToolOfferProvider;
        ArgumentNullException.ThrowIfNull(toolApprovalPolicy);
        _toolApprovalPolicy = toolApprovalPolicy;
    }

    /// <summary>
    ///     The Default Assistant's effective tool offer on <paramref name="modelName" />, or on the node's local default
    ///     chat model when it is blank.
    /// </summary>
    /// <remarks>
    ///     Read-only: it runs the send path's resolution (capabilities, trust boundary, node switches, approval policy)
    ///     without loading a model.
    /// </remarks>
    public async Task<DefaultAssistantToolOffer> GetAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        // The same head a send with no model picked takes: the installed-GGUF local default.
        var activeModel = string.IsNullOrWhiteSpace(modelName)
            ? await _localDefaultChatModelResolver.ResolveAsync((await _nodeSettingsStore.LoadAsync(cancellationToken)).DefaultModelName, cancellationToken)
            : modelName;
        var defaultAgentId = await _defaultAgentProvider.GetDefaultAgentIdAsync(cancellationToken);

        // The form's model field IS the pin being edited, so the queried model wins over the stored one. A null
        // retrieval query takes the playbook fast path, which never builds an embedding client.
        var resolution = await _turnResolver.ResolveAsync(activeModel,
            requiresInstalledChatModel: false,
            defaultAgentId,
            retrievalQuery: null,
            userPickedConcreteModel: true,
            cancellationToken);
        var offer = await ChatToolOfferResolver.ResolveAsync(useLocalTools: true, resolution, _runtimeSettings, _localToolOfferProvider, _toolApprovalPolicy, cancellationToken);

        return new DefaultAssistantToolOffer
        {
            ModelName = resolution.EffectiveModel,
            ToolNames = [.. offer.AllowedTools?.Select(static tool => tool.Name) ?? []]
        };
    }
}
