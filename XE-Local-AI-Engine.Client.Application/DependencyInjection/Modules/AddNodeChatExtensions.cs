namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
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
        // The one provider-routed capability resolution: OrchestrationResolver resolves each participant from its own
        // effective model through it, and ChatTurnResolver resolves the turn's active model through it. Scoped to match
        // IModelClassificationService's lifetime.
        builder.Services.AddScoped<IModelCapabilityResolver, ModelCapabilityResolver>();
        // Derives the server-secret per-conversation seed that nonces the untrusted-content fence around attachment
        // context (keeps the fence un-forgeable by a client that knows only the public conversation id). Singleton: it
        // holds no per-request state and reads the process-lifetime node key.
        builder.Services.AddSingleton<IUntrustedContentFenceSeedProvider, UntrustedContentFenceSeedProvider>();
        // Composes the synthetic attachment / image / knowledge context messages both the send and regenerate paths
        // prepend to a turn. Singleton: it holds no per-request state, and the scoped knowledge search it needs is
        // resolved from a fresh scope per call.
        builder.Services.AddSingleton<IChatTurnContextBuilder, ChatTurnContextBuilder>();
        builder.Services.AddScoped<ILocalDefaultChatModelResolver, LocalDefaultChatModelResolver>();
        builder.Services.AddScoped<ChatTurnResolver>();
        builder.Services.AddScoped<ChatInvocationStatePump>();
        builder.Services.AddScoped<INodeChatStreamService, NodeChatStreamService>();
        builder.Services.AddScoped<INodeChatRegenerationService, NodeChatRegenerationService>();
        builder.Services.AddSingleton<NodeChatRestartRecoveryService>();

        // Non-destructive conversation compaction (manual, local-model summarization). The summarizer is stateless and
        // reads the process-lifetime provider registry (Singleton, mirroring the memory-extraction agent); the
        // orchestrating service depends on the Scoped local-default resolver, so it is Scoped.
        builder.Services.AddOptions<ConversationCompactionOptions>()
               .Bind(configuration.GetSection(ConversationCompactionOptions.SectionName))
               .ValidateDataAnnotations()
               .ValidateOnStart();
        builder.Services.AddSingleton<IConversationSummarizer, ConversationSummarizer>();
        builder.Services.AddScoped<IConversationCompactionService, ConversationCompactionService>();

        return builder;
    }
}
