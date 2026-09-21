namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.Interaction.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal static class AddNodeInvocationExtensions
{
    public static IHostApplicationBuilder AddNodeInvocation(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddSingleton(sp => new Lazy<IWorkerEventDispatcher>(() => sp.GetRequiredService<IWorkerEventDispatcher>()));
        builder.Services.AddSingleton<ModelNameValidator>();
        builder.Services.AddSingleton<IRuntimePackageValidator, RuntimePackageValidator>();
        builder.Services.AddSingleton<INodeAeadCipher, AesGcmNodeAeadCipher>();
        builder.Services.AddOptions<ProviderResilienceOptions>()
               .Bind(configuration.GetSection(ProviderResilienceOptions.SectionName))
               .Validate(static options => options.MaxRetries >= 0
                                           && options.BaseDelayMilliseconds >= 0
                                           && options.MaxDelayMilliseconds >= options.BaseDelayMilliseconds
                                           && options.CircuitBreakerFailureThreshold >= 1
                                           && options.CircuitBreakerBreakDurationSeconds >= 1,
                   "Invalid ProviderResilienceOptions configuration.")
               .ValidateOnStart();
        builder.Services.AddSingleton<IProviderStreamResilience, ProviderStreamResilience>();
        builder.Services.AddOptions<ConversationContextBudgetOptions>()
               .Bind(configuration.GetSection(ConversationContextBudgetOptions.SectionName))
               .Validate(static options => options.ReservedOutputTokenFloor >= 0
                                           && options.RecentTurnKeepCount >= 2
                                           && options.HistoricalToolResultExcerptChars >= 0
                                           && options.DefaultContextTokens >= 1,
                   "Invalid ConversationContextBudgetOptions configuration.")
               .ValidateOnStart();
        builder.Services.TryAddSingleton<ITokenEstimatorCalibrationStore, TokenEstimatorCalibrationStore>();
        builder.Services.AddSingleton(static sp => new LlamaTokenEstimatorCalibrationService(new HttpClient(LlamaTokenEstimatorCalibrationService.CreateProductionHandler(), disposeHandler: true),
            sp.GetRequiredService<ITokenEstimatorCalibrationStore>(),
            sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
            sp.GetRequiredService<ILogger<LlamaTokenEstimatorCalibrationService>>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ITokenEstimatorCalibrationScheduler>(static sp =>
            sp.GetRequiredService<LlamaTokenEstimatorCalibrationService>());
        builder.Services.AddHostedService(static sp => sp.GetRequiredService<LlamaTokenEstimatorCalibrationService>());
        builder.Services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        builder.Services.AddSingleton<IConversationContextBudgeter, ConversationContextBudgeter>();
        builder.Services.AddSingleton<IToolApprovalAuditRecorder, ToolApprovalAuditRecorder>();

        // The ask_user hand-off: the runner writes the operator's answer here after its out-of-stream round-trip and AskUserToolHandler
        // pops it when the approved call executes. Both sides need the SAME instance; the handler is a Singleton for the registry's sake.
        builder.Services.AddSingleton<UserQuestionAnswerStash>();
        builder.Services.AddSingleton<IClientLocalToolHandler, AskUserToolHandler>();
        builder.Services.AddSingleton<IInvocationAttachmentTracker, InvocationAttachmentTracker>();
        builder.Services.AddSingleton<LocalRuntimeWarmer>();

        // Singletons for the same reason the runner is: they own state outliving the turn that created it — a session-approval memo
        // spans a conversation, a parked call is released from another call stack, a cancel must find the live turn. ONE registry, shared.
        builder.Services.AddSingleton<PendingToolCallRegistry>();
        builder.Services.AddSingleton<ToolApprovalCoordinator>();
        builder.Services.AddSingleton<ApiToolCallBridge>();
        builder.Services.AddSingleton<InvocationLifecycleTracker>();
        // SCOPED, and the singleton runner above may not hold it under any wrapper — not even Lazy<T>, which defers construction but
        // never opens a scope. InvocationRunner.RunAsync opens ONE explicit scope per `auto` turn; other efforts never resolve it.
        builder.Services.AddScoped<IReasoningEffortDispatcher, DefaultReasoningEffortDispatcher>();
        builder.Services.AddSingleton<IInvocationRunner, InvocationRunner>();
        builder.Services.AddHostedService<DetachedInvocationReaper>();
        builder.Services.AddSingleton<IInvocationHistory, InvocationHistory>();
        builder.Services.AddSingleton<IWorkerEventDispatcher, WorkerEventDispatcher>();
        builder.Services.AddSingleton<ModelCapabilityProber>();
        builder.Services.AddSingleton<CapabilityReportComposer>();
        builder.Services.AddSingleton<ICapabilityReporter, CapabilityReporter>();
        builder.Services.AddSingleton(sp => new Lazy<ICapabilityReporter>(() => sp.GetRequiredService<ICapabilityReporter>()));
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NodeSqliteKeyHolder>();
        builder.Services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        builder.Services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        builder.Services.AddScoped<INodeRetentionStore, NodeRetentionStore>();

        return builder;
    }
}
