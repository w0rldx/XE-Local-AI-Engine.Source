namespace XE_Local_AI_Engine.AI.Agent.DependencyInjection;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Configuration.Validation;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Instructions.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

public static class AgentServiceCollectionExtensions
{
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

        _ = services.AddSingleton<IValidateOptions<LocalChatAgentOptions>, LocalChatAgentOptionsValidator>();
        _ = services.AddSingleton<IValidateOptions<InvocationAgentOptions>, InvocationAgentOptionsValidator>();

        // Requires a prior IChatClient registration in the host composition root.
        services.DecorateChatClientPipeline();

        _ = services.AddSingleton<IAgentInstructionProvider, AgentInstructionProvider>();
        _ = services.AddSingleton<IAgentToolRegistry, LocalAgentToolRegistry>();
        // Option B: server-driven ClientLocal tools (e.g. run_in_agent_home) resolve through their registered
        // IClientLocalToolHandler implementations. The worker application layer registers the handlers.
        _ = services.AddSingleton<IClientLocalToolRegistry, ClientLocalToolRegistry>();
        // Option C: node-local MCP tools. This registry is MCP-agnostic (holds only AITool); the application layer's
        // connection manager owns the MCP client lifecycle and pushes an immutable snapshot into it as servers connect.
        _ = services.AddSingleton<IMcpToolRegistry, McpToolRegistry>();
        _ = services.AddSingleton<IInvocationAgentFactory, InvocationAgentFactory>();
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
            inner.AsBuilder()
                 .Use(chatClient => new ToolInvocationObservabilityChatClient(chatClient, serviceProvider.GetRequiredService<ILogger<ToolInvocationObservabilityChatClient>>()))
                 .UseFunctionInvocation(serviceProvider.GetRequiredService<ILoggerFactory>())
                 .Build());

        return services;
    }
}
