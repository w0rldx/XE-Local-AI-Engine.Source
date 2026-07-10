namespace XE_Local_AI_Engine.AI.Agent.DependencyInjection;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

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
        _ = services.AddSingleton<IInvocationAgentFactory, InvocationAgentFactory>();
        // Multi-agent handoff orchestration. Reuses the same IChatClient + tool registries as the single-agent
        // factory; confines all Microsoft.Agents.AI.Workflows types behind IOrchestrationRunSession.
        _ = services.AddSingleton<IOrchestrationAgentFactory, OrchestrationAgentFactory>();
        // Playbook eval gate (golden-conversation runner). Stateless: builds a per-call agent over the
        // caller-supplied node-local IChatClient with an empty tool set and runs it threadless.
        _ = services.AddSingleton<IPlaybookEvalAgentRunner, MafPlaybookEvalAgentRunner>();
        // Open Canvas (Preview) workflow runner. Builds a raw MAF WorkflowBuilder over the caller-supplied node-local
        // IChatClient (the caller resolves it per model and hands it in); confines all Microsoft.Agents.AI.Workflows types to the runner.
        _ = services.AddSingleton<IPreviewWorkflowRunner, PreviewWorkflowRunner>();
        return services;
    }

    /// <summary>
    ///     Decorates the registered <see cref="IChatClient" /> with the agent pipeline:
    ///     <see cref="ToolInvocationObservabilityChatClient" /> (tool-call lifecycle events) +
    ///     <c>UseFunctionInvocation</c> (automatic tool execution).
    ///     <para>
    ///         Exposed as a public method so test harnesses that replace the base
    ///         <see cref="IChatClient" /> with a fake (e.g. FakeOllama) can reapply
    ///         the full pipeline decoration after their <c>RemoveAll</c> + <c>AddSingleton</c>.
    ///     </para>
    /// </summary>
    public static IServiceCollection DecorateChatClientPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.Decorate<IChatClient>((inner, serviceProvider) =>
        {
            // Resolve defensively: this method is re-entrant (test harnesses re-apply the pipeline after swapping the
            // base client), and the decoration factory runs lazily at IChatClient resolution. A missing registration
            // falls back to the pinned defaults rather than throwing during a partial re-decoration.
            var pipelineOptions = serviceProvider.GetService<IOptions<AgentToolPipelineOptions>>()?.Value ?? new AgentToolPipelineOptions();

            // First .Use is outermost. OpenTelemetry sits INNERMOST (below function invocation) so each provider round
            // in a tool-calling loop emits its own gen_ai span — the documented MEAI ordering. The source name is pinned
            // explicitly because MEAI's default ("Experimental.Microsoft.Extensions.AI") does NOT match the ServiceDefaults
            // wildcard AddSource/AddMeter("Microsoft.Extensions.AI*"). Sensitive data (prompts/completions) is left OFF.
            return inner.AsBuilder()
                        .Use(chatClient => new ToolInvocationObservabilityChatClient(chatClient, serviceProvider.GetRequiredService<ILogger<ToolInvocationObservabilityChatClient>>()))
                        .UseFunctionInvocation(serviceProvider.GetRequiredService<ILoggerFactory>(),
                            functionInvokingChatClient => functionInvokingChatClient.MaximumIterationsPerRequest = pipelineOptions.MaximumToolIterationsPerRequest)
                        .UseOpenTelemetry(serviceProvider.GetRequiredService<ILoggerFactory>(), sourceName: "Microsoft.Extensions.AI")
                        .Build();
        });

        return services;
    }
}
