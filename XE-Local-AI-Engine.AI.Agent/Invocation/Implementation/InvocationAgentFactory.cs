namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;

internal sealed class InvocationAgentFactory : IInvocationAgentFactory
{
    /// <summary>
    ///     The binary reasoning-"on" sentinel for a model that lacks the Ollama <c>thinking</c> capability but reasons
    ///     by default. "on" — and any graded level (low/medium/high) carried onto such a model — makes the factory OMIT
    ///     the think field so the model's built-in reasoning runs; only "none"/unspecified suppresses it via think:false
    ///     (see <see cref="IsReasoningRequested" />). Thinking-capable models never take this path — they honor
    ///     false/low/medium/high via <see cref="ResolveThinkOption" />.
    /// </summary>
    private const string BinaryReasoningOn = "on";

    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly ILogger<InvocationAgentFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpToolRegistry _mcpToolRegistry;
    private readonly InvocationAgentOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentToolRegistry _toolRegistry;

    public InvocationAgentFactory(IChatClient chatClient,
        IOptions<InvocationAgentOptions> options,
        ILogger<InvocationAgentFactory> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        IMcpToolRegistry mcpToolRegistry)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options.Value;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _clientLocalToolRegistry = clientLocalToolRegistry ?? throw new ArgumentNullException(nameof(clientLocalToolRegistry));
        _mcpToolRegistry = mcpToolRegistry ?? throw new ArgumentNullException(nameof(mcpToolRegistry));
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

        // Ollama think option, by model capability:
        //  - Thinking-capable model: honor the requested effort (false, low, medium, or high).
        //  - Non-thinking model: omit the think field when reasoning is requested (the binary on, OR a graded level
        //    carried onto it by an agent definition or a stale composer selection), and send think false only for
        //    none or unspecified. Sending think true or a level is rejected by Ollama with HTTP 400 (does not support
        //    thinking); think false is accepted but actively suppresses the reasoning some GGUF chat templates (e.g.
        //    unsloth gemma-4-12b-it) emit by default, whereas omitting the field lets that template reasoning through
        //    so the user sees a reasoning block. Verified live against gemma-4-12b-it on Ollama 0.30.5: think false
        //    returns empty thinking, while omitting the field returns populated thinking. Cloud providers default
        //    SupportsThinking to true and ignore the unknown property, so their option dictionary is unchanged.
        var additionalProperties = new AdditionalPropertiesDictionary();
        if (definition.SupportsThinking)
        {
            // Graded reasoning model: honor the requested effort (false / "low" / "medium" / "high").
            additionalProperties["think"] = ResolveThinkOption(definition.ReasoningEffort);
        }
        else if (IsReasoningRequested(definition.ReasoningEffort))
        {
            // Non-thinking model, reasoning requested (binary "on" OR a graded low/medium/high carried onto a model
            // that cannot do graded thinking): OMIT the think field entirely so the model's default (chat-template-
            // baked) reasoning is allowed through. We must NOT send think:true or a level — Ollama returns HTTP 400
            // ("does not support thinking") for a model without the thinking capability; only omission lets the
            // built-in reasoning run. The key is therefore intentionally left out here.
        }
        else
        {
            // Non-thinking model, reasoning OFF ("none") or unspecified (the safe default for agent/legacy paths):
            // think:false actively suppresses the reasoning some GGUF templates emit by default.
            additionalProperties["think"] = false;
        }

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
                    AdditionalProperties = additionalProperties
                }
            }
        };

        context.Items["modelId"] = definition.ModelId;
        context.Items["toolsEnabled"] = tools.Count > 0;

        return Task.FromResult(context);
    }

    /// <summary>
    ///     Intersects the offer list the definition carries with the executable catalogs, matched by name (Option A
    ///     built-in / B ClientLocal / C MCP). Offered names are sourced from <c>definition.Tools</c> (which the runner
    ///     builds from the runtime package's allowed-tool list). Delegates to the shared
    ///     <see cref="InvocationToolResolver" /> so the single-agent and orchestration factories resolve tools
    ///     identically.
    /// </summary>
    private IList<AITool> ResolveExecutableTools(InvocationAgentDefinition definition)
    {
        return InvocationToolResolver.Resolve(definition.Tools, _toolRegistry, _clientLocalToolRegistry, _mcpToolRegistry, _logger);
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

        // Default to think:true (reason). This also intentionally covers the "on" binary-reasoning sentinel
        // (<see cref="BinaryReasoningOn" />): it is normally handled in the !SupportsThinking branch, and only reaches
        // this graded path defensively (the React clamp keeps "on" off thinking-capable models) — where "reason" is the
        // right meaning for a model that can think.
        return true;
    }

    /// <summary>
    ///     True when the effort asks the model to reason: the binary <see cref="BinaryReasoningOn" /> sentinel or a
    ///     graded level (low/medium/high). Used ONLY on the non-thinking-model branch — a graded level can be carried
    ///     onto a model that lacks the Ollama <c>thinking</c> capability (an agent definition pins it, or the composer
    ///     keeps a stale selection across a model switch). The model cannot honor the graded level (Ollama 400s on
    ///     <c>think:&lt;level&gt;</c>), but the user still asked to reason, so the caller OMITS the think field and lets
    ///     the model's built-in reasoning run. Only <c>none</c> (or unspecified/blank) returns false → think:false.
    /// </summary>
    private static bool IsReasoningRequested(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return false;
        }

        var normalized = reasoningEffort.Trim();
        return string.Equals(normalized, BinaryReasoningOn, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase);
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
