namespace XE_Local_AI_Engine.AI.Agent.DependencyInjection;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Configuration.Validation;
using XE_Local_AI_Engine.AI.Agent.Eval;
using XE_Local_AI_Engine.AI.Agent.Eval.Implementation;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Instructions.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Composition-root extensions for the local AI agent runtime.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    ///     Registers option validation, prompt/tool registries, single-agent invocation, and handoff orchestration.
    /// </summary>
    /// <remarks>
    ///     The host must register the base <see cref="IChatClient" /> before calling this method. This method then
    ///     decorates that client with tool-observability and automatic function invocation so local chat, platform
    ///     invocations, ClientLocal tools, and MCP tools all share the same execution pipeline.
    /// </remarks>
    public static IServiceCollection AddLocalAiAgentRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        _ = services.AddOptions<LocalChatAgentOptions>()
                    .Bind(configuration.GetSection(LocalChatAgentOptions.Section))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

        _ = services.AddOptions<InvocationAgentOptions>()
                    .Bind(configuration.GetSection(InvocationAgentOptions.Section))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

        _ = services.AddOptions<OrchestrationAgentOptions>()
                    .Bind(configuration.GetSection(OrchestrationAgentOptions.Section))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

        _ = services.AddOptions<AgentToolPipelineOptions>()
                    .Bind(configuration.GetSection(AgentToolPipelineOptions.Section))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

        // Code-owned gen_ai telemetry policy: CaptureSensitiveContent defaults false and is set EXPLICITLY below, so the
        // ambient OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT Aspire injects cannot silently turn capture on.
        _ = services.AddOptions<AgentTelemetryOptions>()
                    .Bind(configuration.GetSection(AgentTelemetryOptions.Section))
                    .ValidateOnStart();

        // Provider-boundary budgeting: per-round input budgets plus cumulative ceilings applied by the innermost hop, so
        // the inner tool loop and MAF participant rounds are bounded, not just the two outer history-growth points.
        _ = services.AddOptions<ProviderCallBudgetOptions>()
                    .Bind(configuration.GetSection(ProviderCallBudgetOptions.Section))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

        // Per-round tool-relevance offer: thresholds and embedding knobs only. Whether it engages is the node setting
        // ToolRelevanceEnabled, read live per turn and off by default, leaving the hop a reference-equality passthrough.
        _ = services.AddOptions<ToolRelevanceOptions>()
                    .Bind(configuration.GetSection(ToolRelevanceOptions.Section))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

        _ = services.AddSingleton<IValidateOptions<LocalChatAgentOptions>, LocalChatAgentOptionsValidator>();
        _ = services.AddSingleton<IValidateOptions<InvocationAgentOptions>, InvocationAgentOptionsValidator>();
        _ = services.AddSingleton<IValidateOptions<OrchestrationAgentOptions>, OrchestrationAgentOptionsValidator>();
        _ = services.AddSingleton<IValidateOptions<AgentToolPipelineOptions>, AgentToolPipelineOptionsValidator>();

        // Requires a prior IChatClient registration in the host composition root.
        services.DecorateChatClientPipeline();

        _ = services.AddSingleton<IAgentInstructionProvider, AgentInstructionProvider>();
        _ = services.AddSingleton<IAgentToolRegistry, LocalAgentToolRegistry>();
        // Server-driven ClientLocal tools (for example run_in_agent_home) resolve through registered
        // IClientLocalToolHandler implementations. The worker application layer registers the handlers.
        _ = services.AddSingleton<IClientLocalToolRegistry, ClientLocalToolRegistry>();
        // Node-local MCP tools. This registry is MCP-agnostic (holds only AITool); the application layer's
        // connection manager owns the MCP client lifecycle and pushes an immutable snapshot into it as servers connect.
        _ = services.AddSingleton<IMcpToolRegistry, McpToolRegistry>();
        // Tool-approval policy floor: the identity no-op, so a host without the node-configured NodeToolApprovalPolicy
        // still resolves one. TryAddSingleton, so the composition root's plain AddSingleton wins under last-wins.
        services.TryAddSingleton<IToolApprovalPolicy, PermissiveToolApprovalPolicy>();
        // Tool-relevance ranker: the deterministic, model-free lexical selector is the shipped default and the fallback
        // every other implementation degrades to; the node composition root Replaces it with the embedding-backed one.
        services.TryAddSingleton<IToolRelevanceSelector, LexicalToolRelevanceSelector>();
        _ = services.AddSingleton<IInvocationAgentFactory, InvocationAgentFactory>();
        // Multi-agent handoff orchestration. Reuses the same IChatClient + tool registries as the single-agent
        // factory; confines all Microsoft.Agents.AI.Workflows types behind IOrchestrationRunSession.
        _ = services.AddSingleton<IOrchestrationAgentFactory, OrchestrationAgentFactory>();
        // Playbook eval gate (golden-conversation runner). Stateless: builds a per-call agent over the
        // caller-supplied node-local IChatClient with an empty tool set and runs it threadless.
        _ = services.AddSingleton<IPlaybookEvalAgentRunner, MafPlaybookEvalAgentRunner>();
        return services;
    }

    /// <summary>
    ///     Decorates the registered <see cref="IChatClient" /> with the agent pipeline, outermost first: tool
    ///     observability, function invocation, tool relevance, provider-call budgeting, OpenTelemetry.
    /// </summary>
    /// <remarks>
    ///     Exposed as a public method so test harnesses that replace the base <see cref="IChatClient" /> with a fake
    ///     can reapply the full decoration after their <c>RemoveAll</c> + <c>AddSingleton</c>. The order is
    ///     load-bearing; see docs/wiki/04-agent-mode.md ("Why each hop sits where it does, and what it may mutate").
    /// </remarks>
    public static IServiceCollection DecorateChatClientPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITokenEstimatorCalibrationStore, TokenEstimatorCalibrationStore>();

        _ = services.Decorate<IChatClient>((inner, serviceProvider) =>
        {
            // Resolve defensively: this method is re-entrant and the factory runs lazily at IChatClient resolution, so a
            // missing registration falls back to the pinned defaults rather than throwing mid re-decoration.
            var pipelineOptions = serviceProvider.GetService<IOptions<AgentToolPipelineOptions>>()?.Value ?? new AgentToolPipelineOptions();
            var telemetryOptions = serviceProvider.GetService<IOptions<AgentTelemetryOptions>>()?.Value ?? new AgentTelemetryOptions();
            var toolRelevanceOptions = serviceProvider.GetService<IOptions<ToolRelevanceOptions>>()?.Value ?? new ToolRelevanceOptions();
            var toolRelevanceSelector = serviceProvider.GetService<IToolRelevanceSelector>() ?? new LexicalToolRelevanceSelector();

            // Enabling sensitive-content capture is a privacy-sensitive, deliberate opt-in — surface it loudly once so an
            // operator can never leave full prompts/reasoning/completions flowing into telemetry unnoticed.
            if (telemetryOptions.CaptureSensitiveContent)
            {
                serviceProvider.GetRequiredService<ILogger<OpenTelemetryChatClient>>()
                               .LogWarning("Agent gen_ai telemetry is capturing SENSITIVE message content (prompts, reasoning, completions, tool arguments) into spans because "
                                           + "{Section}:{Setting} is enabled. Disable it for any environment where telemetry is exported off-box.",
                                   AgentTelemetryOptions.Section,
                                   nameof(AgentTelemetryOptions.CaptureSensitiveContent));
            }

            // First .Use is outermost, OpenTelemetry INNERMOST so each provider round emits its own gen_ai span. Source
            // name and EnableSensitiveData are pinned; see docs/wiki/04-agent-mode.md, "The chat-client decorator pipeline".
            var providerClient = inner.AsBuilder()
                        .Use(chatClient => new ToolRelevanceChatClient(chatClient,
                            toolRelevanceSelector,
                            toolRelevanceOptions,
                            serviceProvider.GetRequiredService<ILogger<ToolRelevanceChatClient>>()))
                        .Use(chatClient => new ProviderCallBudgetChatClient(chatClient,
                            serviceProvider.GetRequiredService<ILogger<ProviderCallBudgetChatClient>>(),
                            serviceProvider.GetRequiredService<ITokenEstimatorCalibrationStore>()))
                        .UseOpenTelemetry(serviceProvider.GetRequiredService<ILoggerFactory>(),
                            sourceName: "Microsoft.Extensions.AI",
                            configure: openTelemetryChatClient => openTelemetryChatClient.EnableSensitiveData = telemetryOptions.CaptureSensitiveContent)
                        .Build();
            var functionInvokingClient = providerClient.AsBuilder()
                        .UseFunctionInvocation(serviceProvider.GetRequiredService<ILoggerFactory>(),
                            functionInvokingChatClient =>
                            {
                                functionInvokingChatClient.MaximumIterationsPerRequest = pipelineOptions.MaximumToolIterationsPerRequest;
                                // Keep these shared recovery/privacy/concurrency policies fixed rather than operator-tunable, and pin them against upgrade drift.
                                functionInvokingChatClient.MaximumConsecutiveErrorsPerRequest = 3;
                                functionInvokingChatClient.IncludeDetailedErrors = false;
                                functionInvokingChatClient.AllowConcurrentInvocation = false;
                                functionInvokingChatClient.TerminateOnUnknownCalls = false;
                            })
                        .Build();

            return new ToolInvocationObservabilityChatClient(
                new EmptyToolOfferChatClient(functionInvokingClient, providerClient),
                serviceProvider.GetRequiredService<ILogger<ToolInvocationObservabilityChatClient>>());
        });

        return services;
    }
}
