namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalChatRuntimePackageBuilderTests
{
    [Test]
    public void Build_WithMinimalRequest_UsesLoopbackDefaults()
    {
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var builder = new LocalChatRuntimePackageBuilder();

        var package = builder.Build(new LocalChatRuntimePackageRequest(invocationId,
            conversationId,
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", 0)],
            "qwen3.5:0.8b",
            1));

        AssertEx.Equal(invocationId, package.InvocationId);
        AssertEx.Equal(conversationId, package.ConversationId);
        AssertEx.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), package.ClientNodeId);
        AssertEx.Equal("qwen3.5:0.8b", package.ModelProfile);
        AssertEx.Equal(1, package.AgentDefinitionVersion);
        AssertEx.Empty(package.AllowedTools);
        AssertEx.Null(package.ToolPolicies);
        AssertEx.Null(package.RequestedCapabilities);
        AssertEx.Equal(300, package.Timeouts.InvocationTimeoutSeconds);
        AssertEx.Equal(30, package.Timeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(60, package.Timeouts.StreamIdleTimeoutSeconds);
        AssertEx.False(string.IsNullOrWhiteSpace(package.ConfigHash));
    }

    [Test]
    public void Build_OrdersConversationMessagesBySortOrder()
    {
        var builder = new LocalChatRuntimePackageBuilder();

        var package = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            "You are helpful.",
            [
                CreateMessage(MessageRole.Assistant, "third", 2),
                CreateMessage(MessageRole.User, "first", 0),
                CreateMessage(MessageRole.Assistant, "second", 1)
            ],
            "qwen3.5:0.8b",
            1));

        AssertEx.Equal("first", package.ConversationContext[0].Content);
        AssertEx.Equal("second", package.ConversationContext[1].Content);
        AssertEx.Equal("third", package.ConversationContext[2].Content);
    }

    [Test]
    public void Build_WithExplicitOptions_PreservesRequestedMetadataAndComputesStableConfigHash()
    {
        var builder = new LocalChatRuntimePackageBuilder();
        var allowedTool = new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = "open_url",
            Location = ToolLocation.ApiSide,
            ParameterSchema = "{\"type\":\"object\"}"
        };
        var request = new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", 0)],
            "qwen3.5:0.8b",
            3,
            Guid.NewGuid(),
            [allowedTool],
            new Dictionary<string, object>
            {
                ["approvalRequired"] = true
            },
            ["local-chat", "loopback"],
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 45,
                ToolCallTimeoutSeconds = 15,
                StreamIdleTimeoutSeconds = 20
            },
            "high");

        var package = builder.Build(request);

        AssertEx.Equal(1, package.AllowedTools.Count);
        AssertEx.Equal("open_url", package.AllowedTools[0].Name);
        AssertEx.Equal(true, package.ToolPolicies!["approvalRequired"]);
        AssertEx.Equal("high", package.ReasoningEffort);
        AssertEx.Equal(2, AssertEx.NotNull(package.RequestedCapabilities).Count);

        var expectedHash = RuntimePackageConfigHash.Compute(request.AgentDefinitionVersion,
            request.ResolvedSystemPrompt,
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = allowedTool.Name,
                    Description = null,
                    Schema = allowedTool.ParameterSchema
                }
            ],
            request.ModelProfile,
            AssertEx.NotNull(request.Timeouts),
            request.ReasoningEffort);

        AssertEx.Equal(expectedHash, package.ConfigHash);
    }

    private static ConversationMessageDto CreateMessage(MessageRole role, string content, int sortOrder)
    {
        return new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = role,
            Content = content,
            SortOrder = sortOrder
        };
    }
}
