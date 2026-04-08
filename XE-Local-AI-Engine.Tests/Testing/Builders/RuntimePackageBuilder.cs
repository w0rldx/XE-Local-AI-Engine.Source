namespace XE_Local_AI_Engine.Tests.Testing.Builders;

using XE_Local_AI_Engine.Models;
using XE_Local_AI_Engine.Models.Enums;

public sealed class RuntimePackageBuilder
{
    private readonly List<ConversationMessageDto> _conversationContext = [];
    private readonly List<AllowedToolDto> _allowedTools = [];
    private readonly List<string> _requestedCapabilities = [];
    private readonly Dictionary<string, object> _toolPolicies = [];

    private Guid _invocationId = Guid.NewGuid();
    private Guid _conversationId = Guid.NewGuid();
    private Guid _clientNodeId = Guid.NewGuid();
    private int _agentDefinitionVersion = 1;
    private string _resolvedSystemPrompt = "You are helpful.";
    private string? _modelProfile = "qwen3.5:9b";
    private TimeoutSettings _timeouts = new();
    private string _configHash = "test-config-hash";

    private RuntimePackageBuilder()
    {
        _conversationContext.Add(new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = "Hello",
            SortOrder = 0,
        });
    }

    public static RuntimePackageBuilder Valid() => new();

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
            SortOrder = 0,
        });

        return this;
    }

    public RuntimePackageBuilder WithConversationMessage(
        MessageRole role,
        string content,
        int sortOrder,
        string? toolCalls = null,
        string? toolResults = null,
        string? modelUsed = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        _conversationContext.Add(new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = role,
            Content = content,
            ToolCalls = toolCalls,
            ToolResults = toolResults,
            ModelUsed = modelUsed,
            SortOrder = sortOrder,
        });

        return this;
    }

    public RuntimePackageBuilder WithAllowedTool(
        string name,
        ToolLocation location = ToolLocation.ApiSide,
        string? parameterSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _allowedTools.Add(new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = location,
            ParameterSchema = parameterSchema,
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
            StreamIdleTimeoutSeconds = streamIdleSeconds,
        };

        return this;
    }

    public RuntimePackageBuilder WithConfigHash(string configHash)
    {
        ArgumentNullException.ThrowIfNull(configHash);
        _configHash = configHash;
        return this;
    }

    public RuntimePackage Build()
    {
        return new RuntimePackage
        {
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
            ConfigHash = _configHash,
        };
    }
}
