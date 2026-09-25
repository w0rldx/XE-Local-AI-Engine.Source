namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
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
        builder.Services.AddSingleton<INodeChatMutationGuard, NodeChatMutationGuard>();
        builder.Services.AddSingleton<INodeChatStreamCancellationRegistry, NodeChatStreamCancellationRegistry>();
        builder.Services.AddSingleton<IInvocationResumeRegistry, InvocationResumeRegistry>();
        builder.Services.AddSingleton<IGgufModelCapabilityResolver, GgufModelCapabilityResolver>();
        // The one provider-routed capability resolution: OrchestrationResolver resolves each participant from its own effective
        // model through it and ChatTurnResolver the turn's active model. Scoped to match IModelClassificationService's lifetime.
        builder.Services.AddScoped<IModelCapabilityResolver, ModelCapabilityResolver>();
        // Derives the server-secret per-conversation seed that nonces the untrusted-content fence around attachment context, so
        // the fence stays un-forgeable by a client that knows only the public conversation id. Singleton over the node key.
        builder.Services.AddSingleton<IUntrustedContentFenceSeedProvider, UntrustedContentFenceSeedProvider>();
        // Composes the synthetic attachment / image / knowledge context messages both the send and regenerate paths prepend to a
        // turn. Singleton: no per-request state, and the scoped knowledge search it needs comes from a fresh scope per call.
        builder.Services.AddSingleton<IChatTurnContextBuilder, ChatTurnContextBuilder>();
        builder.Services.AddScoped<ILocalDefaultChatModelResolver, LocalDefaultChatModelResolver>();
        builder.Services.AddScoped<ChatTurnResolver>();
        builder.Services.AddScoped<ChatInvocationStatePump>();
        builder.Services.AddScoped<INodeChatStreamService, NodeChatStreamService>();
        builder.Services.AddScoped<INodeChatRegenerationService, NodeChatRegenerationService>();
        builder.Services.AddSingleton<NodeChatRestartRecoveryService>();

        // Non-destructive conversation compaction (manual, local-model summarization): the summarizer is stateless over the
        // process-lifetime provider registry (Singleton), and the orchestrating service is Scoped like the resolver it takes.
        builder.Services.AddOptions<ConversationCompactionOptions>()
               .Bind(configuration.GetSection(ConversationCompactionOptions.SectionName))
               .ValidateDataAnnotations()
               .ValidateOnStart();
        builder.Services.AddSingleton<IConversationSummarizer, ConversationSummarizer>();
        builder.Services.AddSingleton<IConversationStateDistiller, ConversationStateDistiller>();
        builder.Services.AddScoped<IConversationStateDistillationService, ConversationStateDistillationService>();
        builder.Services.AddScoped<IConversationCompactionService, ConversationCompactionService>();

        // Automatic post-turn compaction: the chat hooks enqueue onto one bounded queue that a single background worker drains,
        // each job in its own scope, so a fold never runs on (or outlives) the request scope that produced the terminal.
        builder.Services.AddSingleton<ConversationMaintenanceDispatcher>();
        builder.Services.AddSingleton<IConversationMaintenanceDispatcher>(sp => sp.GetRequiredService<ConversationMaintenanceDispatcher>());
        builder.Services.AddHostedService(sp => new ConversationMaintenanceWorker(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ConversationMaintenanceDispatcher>(),
            sp.GetRequiredService<IOptions<ConversationCompactionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ConversationMaintenanceWorker>>()));

        // Chat retention defaults to disabled because it permanently deletes user chat history: it must be opted into through the
        // ChatRetention section, and start-up validation rejects a window of zero or fewer days, whose cutoff would purge everything.
        builder.Services.AddOptions<ChatRetentionOptions>()
               .Bind(builder.Configuration.GetSection(ChatRetentionOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        // The sweeper itself. It resolves the retention, uploaded-file and work-session stores from a per-sweep scope,
        // and only this layer may reach a persistence store, so it is registered here rather than in the host.
        builder.Services.AddHostedService<RetentionSweeperService>();

        return builder;
    }
}
