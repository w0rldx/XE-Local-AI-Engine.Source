namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;

internal static class AddNodeChatExtensions
{
    public static IHostApplicationBuilder AddNodeChat(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddSingleton<NodeChatPersistenceWriter>();
        builder.Services.AddSingleton<INodeChatPersistenceService, NodeChatPersistenceService>();
        builder.Services.AddSingleton<INodeChatInvocationPump, NodeChatInvocationPump>();
        builder.Services.AddSingleton<INodeChatRemotePersistenceCoordinator, NodeChatRemotePersistenceCoordinator>();
        builder.Services.AddSingleton<INodeChatMutationGuard, NodeChatMutationGuard>();
        builder.Services.AddSingleton<INodeChatStreamCancellationRegistry, NodeChatStreamCancellationRegistry>();
        builder.Services.AddSingleton<IInvocationResumeRegistry, InvocationResumeRegistry>();
        builder.Services.AddSingleton<IGgufModelCapabilityResolver, GgufModelCapabilityResolver>();
        // Provider-routed (thinking/tools) capability resolution used by OrchestrationResolver to resolve each
        // participant's thinking capability from its own effective model (mirrors ChatTurnResolver's active-model
        // resolution). Scoped to match IModelClassificationService's lifetime.
        builder.Services.AddScoped<IModelCapabilityResolver, ModelCapabilityResolver>();
        builder.Services.AddScoped<ILocalDefaultChatModelResolver, LocalDefaultChatModelResolver>();
        builder.Services.AddScoped<ChatTurnResolver>();
        builder.Services.AddScoped<ChatInvocationStatePump>();
        builder.Services.AddScoped<INodeChatStreamService, NodeChatStreamService>();
        builder.Services.AddScoped<INodeChatRegenerationService, NodeChatRegenerationService>();
        builder.Services.AddSingleton<NodeChatRestartRecoveryService>();

        return builder;
    }
}
