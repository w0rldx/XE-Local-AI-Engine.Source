namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;

internal sealed class InvocationAgentFactory : IInvocationAgentFactory
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<InvocationAgentFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly InvocationAgentOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentToolRegistry _toolRegistry;

    public InvocationAgentFactory(IChatClient chatClient,
        IOptions<InvocationAgentOptions> options,
        ILogger<InvocationAgentFactory> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IAgentToolRegistry toolRegistry)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options.Value;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
    }

    public Task<InvocationAgentContext> CreateAsync(InvocationAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var tools = ResolveExecutableTools(definition);

        _logger.LogDebug("Creating invocation agent context for model {ModelId}.", definition.ModelId);

        var agent = new ChatClientAgent(_chatClient,
            $"{_options.AgentNamePrefix}-{definition.ModelId}",
            definition.Instructions,
            "XE Local AI Engine worker invocation agent.",
            tools,
            _loggerFactory,
            _serviceProvider);

        var seedMessages = BuildSeedMessages(definition);

        InvocationAgentContext context = new()
        {
            Agent = agent,
            Session = null,
            SeedMessages = seedMessages,
            RunOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    ModelId = definition.ModelId,
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["think"] = ResolveThinkOption(definition.ReasoningEffort)
                    }
                }
            }
        };

        context.Items["modelId"] = definition.ModelId;
        context.Items["toolsEnabled"] = tools.Count > 0;

        return Task.FromResult(context);
    }

    /// <summary>
    /// Intersects the offer list the definition carries with the executable catalog in <see cref="_toolRegistry"/>.
    /// Offered names are sourced from <c>definition.Tools</c> (which the runner builds from the runtime package's
    /// allowed-tool list); the executable bodies always come from the registry, matched by name. Offered names with
    /// no registry match are skipped so a stale or unknown offer can never reach the agent.
    /// </summary>
    private IList<AITool> ResolveExecutableTools(InvocationAgentDefinition definition)
    {
        if (definition.Tools.Count == 0)
        {
            return [];
        }

        var offeredNames = definition.Tools
                                     .Select(static tool => tool.Name)
                                     .Where(static name => !string.IsNullOrWhiteSpace(name))
                                     .ToHashSet(StringComparer.Ordinal);

        List<AITool> resolved = [.. _toolRegistry.GetLocalChatTools()
                                                 .Where(tool => offeredNames.Contains(tool.Name))];

        var skipped = offeredNames.Count - resolved.Count;
        if (skipped > 0)
        {
            _logger.LogDebug("Skipped {SkippedCount} offered tool(s) with no registered executable.", skipped);
        }

        return resolved;
    }

    private static object ResolveThinkOption(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return true;
        }

        var normalized = reasoningEffort.Trim();
        if (string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase))
        {
            return "low";
        }

        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        if (string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase))
        {
            return "high";
        }

        return true;
    }

    private static IReadOnlyList<ChatMessage> BuildSeedMessages(InvocationAgentDefinition definition)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, definition.Instructions)
        ];

        messages.AddRange(definition.ConversationContext);

        return messages;
    }
}
