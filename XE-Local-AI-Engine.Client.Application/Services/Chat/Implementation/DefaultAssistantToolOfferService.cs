namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IDefaultAssistantToolOfferService" />: the send path's model head, turn resolution and
///     tool-offer gate, stopped before anything is persisted or run.
/// </summary>
internal sealed class DefaultAssistantToolOfferService : IDefaultAssistantToolOfferService
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
        _turnResolver = turnResolver ?? throw new ArgumentNullException(nameof(turnResolver));
        _defaultAgentProvider = defaultAgentProvider ?? throw new ArgumentNullException(nameof(defaultAgentProvider));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _localDefaultChatModelResolver = localDefaultChatModelResolver ?? throw new ArgumentNullException(nameof(localDefaultChatModelResolver));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
        _toolApprovalPolicy = toolApprovalPolicy ?? throw new ArgumentNullException(nameof(toolApprovalPolicy));
    }

    public async Task<DefaultAssistantToolOffer> GetAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        // The same head a send with no model picked takes: the installed-GGUF local default, never Ollama.
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
