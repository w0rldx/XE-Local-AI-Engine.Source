namespace XE_Local_AI_Engine.Tests.Testing.Builders;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;

public sealed class RuntimePackageBuilder
{
    private readonly List<AllowedToolDto> _allowedTools = [];
    private readonly List<ConversationMessageDto> _conversationContext = [];
    private readonly List<string> _requestedCapabilities = [];
    private readonly Dictionary<string, object> _toolPolicies = [];
    private int _agentDefinitionVersion = 1;
    private Guid _clientNodeId = Guid.NewGuid();
    private string _configHash = "test-config-hash";
    private Guid _conversationId = Guid.NewGuid();

    private Guid _invocationId = Guid.NewGuid();
    private bool _isUnattended;
    private string? _modelProfile = "qwen3.5:0.8b";
    private string? _reasoningEffort;
    private bool _allowAutoModelSwap;
    private OrchestrationSpec? _orchestrationSpec;
    private string _resolvedSystemPrompt = "You are helpful.";
    private SamplingOptions? _samplingOptions;
    private IReadOnlyList<ResolvedSkill>? _skills;
    private IReadOnlyList<ResolvedCustomTool>? _customTools;
    private TimeoutSettings _timeouts = new();

    private RuntimePackageBuilder()
    {
        _conversationContext.Add(new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = "Hello",
            SortOrder = 0
        });
    }

    public static RuntimePackageBuilder Valid()
    {
        return new RuntimePackageBuilder();
    }

    public RuntimePackageBuilder WithInvocationId(Guid invocationId)
    {
        _invocationId = invocationId;
        return this;
    }

    public RuntimePackageBuilder WithConversationId(Guid conversationId)
    {
        _conversationId = conversationId;
        return this;
    }

    public RuntimePackageBuilder WithClientNodeId(Guid clientNodeId)
    {
        _clientNodeId = clientNodeId;
        return this;
    }

    public RuntimePackageBuilder WithAgentDefinitionVersion(int agentDefinitionVersion)
    {
        _agentDefinitionVersion = agentDefinitionVersion;
        return this;
    }

    public RuntimePackageBuilder WithSystemPrompt(string systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        _resolvedSystemPrompt = systemPrompt;
        return this;
    }

    public RuntimePackageBuilder WithModel(string? modelProfile)
    {
        _modelProfile = modelProfile;
        return this;
    }

    public RuntimePackageBuilder WithUserMessage(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _conversationContext.Clear();
        _conversationContext.Add(new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
            SortOrder = 0
        });

        return this;
    }

    public RuntimePackageBuilder WithImageMessage(string content, string mediaType, byte[] data, int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(data);

        _conversationContext.Add(new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
            SortOrder = sortOrder,
            Images = [new ConversationImagePart(mediaType, data)]
        });

        return this;
    }

    public RuntimePackageBuilder WithConversationMessage(MessageRole role,
        string content,
        int sortOrder,
        string? modelUsed = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        _conversationContext.Add(new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = role,
            Content = content,
            ModelUsed = modelUsed,
            SortOrder = sortOrder
        });

        return this;
    }

    /// <summary>
    ///     An assistant turn carrying replayed tool history: the call/result pairs a caller-managed continuation sends
    ///     ahead of the turn's own text. <paramref name="content" /> may be blank — that is the shape of a run that
    ///     called a tool and then died.
    /// </summary>
    public RuntimePackageBuilder WithToolExchangeMessage(string content, int sortOrder, params ConversationToolExchange[] exchanges)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(exchanges);

        _conversationContext.Add(new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.Assistant,
            Content = content,
            SortOrder = sortOrder,
            ToolExchanges = exchanges
        });

        return this;
    }

    public RuntimePackageBuilder WithAllowedTool(string name,
        ToolLocation location = ToolLocation.ApiSide,
        string? parameterSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _allowedTools.Add(new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = location,
            ParameterSchema = parameterSchema
        });

        return this;
    }

    public RuntimePackageBuilder WithRequestedCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        _requestedCapabilities.Add(capability);
        return this;
    }

    public RuntimePackageBuilder WithToolPolicy(string key, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _toolPolicies[key] = value;
        return this;
    }

    public RuntimePackageBuilder WithTimeout(int invocationSeconds = 300, int toolCallSeconds = 30, int streamIdleSeconds = 60)
    {
        _timeouts = new TimeoutSettings
        {
            InvocationTimeoutSeconds = invocationSeconds,
            ToolCallTimeoutSeconds = toolCallSeconds,
            StreamIdleTimeoutSeconds = streamIdleSeconds
        };

        return this;
    }

    public RuntimePackageBuilder WithConfigHash(string configHash)
    {
        ArgumentNullException.ThrowIfNull(configHash);
        _configHash = configHash;
        return this;
    }

    public RuntimePackageBuilder WithOrchestrationSpec(OrchestrationSpec orchestrationSpec)
    {
        ArgumentNullException.ThrowIfNull(orchestrationSpec);
        _orchestrationSpec = orchestrationSpec;
        return this;
    }

    public RuntimePackageBuilder WithSamplingOptions(SamplingOptions samplingOptions)
    {
        ArgumentNullException.ThrowIfNull(samplingOptions);
        _samplingOptions = samplingOptions;
        return this;
    }

    public RuntimePackageBuilder WithSkills(params ResolvedSkill[] skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        _skills = [.. skills];
        return this;
    }

    public RuntimePackageBuilder WithCustomTools(params ResolvedCustomTool[] customTools)
    {
        ArgumentNullException.ThrowIfNull(customTools);
        _customTools = [.. customTools];
        return this;
    }

    /// <summary>The AUTHORED reasoning effort, normalized by the package builder in production. <c>auto</c> is the one the runner dispatches.</summary>
    public RuntimePackageBuilder WithReasoningEffort(string? reasoningEffort)
    {
        _reasoningEffort = reasoningEffort;
        return this;
    }

    /// <summary>Model-selection provenance: the node picked the model, so the dispatcher may replace it.</summary>
    public RuntimePackageBuilder AllowingAutoModelSwap()
    {
        _allowAutoModelSwap = true;
        return this;
    }

    /// <summary>Marks the package as a scheduled/headless run, which has no operator to answer an approval.</summary>
    public RuntimePackageBuilder AsUnattended()
    {
        _isUnattended = true;
        return this;
    }

    public RuntimePackage Build()
    {
        return new RuntimePackage
        {
            IsUnattended = _isUnattended,
            ReasoningEffort = _reasoningEffort,
            AllowAutoModelSwap = _allowAutoModelSwap,
            Skills = _skills,
            CustomTools = _customTools,
            InvocationId = _invocationId,
            ConversationId = _conversationId,
            ClientNodeId = _clientNodeId,
            AgentDefinitionVersion = _agentDefinitionVersion,
            ResolvedSystemPrompt = _resolvedSystemPrompt,
            ConversationContext = _conversationContext.OrderBy(message => message.SortOrder).ToList(),
            AllowedTools = [.. _allowedTools],
            ToolPolicies = _toolPolicies.Count == 0 ? null : new Dictionary<string, object>(_toolPolicies),
            ModelProfile = _modelProfile,
            RequestedCapabilities = _requestedCapabilities.Count == 0 ? null : [.. _requestedCapabilities],
            Timeouts = _timeouts,
            OrchestrationSpec = _orchestrationSpec,
            SamplingOptions = _samplingOptions,
            ConfigHash = _configHash
        };
    }
}
