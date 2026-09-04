namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
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
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1));

        AssertEx.Equal(invocationId, package.InvocationId);
        AssertEx.Equal(conversationId, package.ConversationId);
        AssertEx.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), package.ClientNodeId);
        AssertEx.Equal("qwen3.5:0.8b", package.ModelProfile);
        AssertEx.Equal(expected: 1, package.AgentDefinitionVersion);
        AssertEx.Empty(package.AllowedTools);
        AssertEx.Null(package.ToolPolicies);
        AssertEx.Null(package.RequestedCapabilities);
        AssertEx.Equal(expected: 600, package.Timeouts.InvocationTimeoutSeconds);
        AssertEx.Equal(expected: 30, package.Timeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(expected: 60, package.Timeouts.StreamIdleTimeoutSeconds);
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
                CreateMessage(MessageRole.Assistant, "third", sortOrder: 2),
                CreateMessage(MessageRole.User, "first", sortOrder: 0),
                CreateMessage(MessageRole.Assistant, "second", sortOrder: 1)
            ],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1));

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
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 3,
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

        AssertEx.Equal(expected: 1, package.AllowedTools.Count);
        AssertEx.Equal("open_url", package.AllowedTools[0].Name);
        AssertEx.Equal(expected: true, package.ToolPolicies!["approvalRequired"]);
        AssertEx.Equal("high", package.ReasoningEffort);
        AssertEx.Equal(expected: 2, AssertEx.NotNull(package.RequestedCapabilities).Count);

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

    [Test]
    [Arguments("on")]
    [Arguments("On")]
    [Arguments("ON")]
    public void Build_WhenReasoningEffortIsBinaryOn_PreservesOnSentinel(string reasoningEffort)
    {
        var builder = new LocalChatRuntimePackageBuilder();

        var package = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1,
            ReasoningEffort: reasoningEffort));

        AssertEx.Equal("on", package.ReasoningEffort);
    }

    [Test]
    public void Build_WhenReasoningEffortIsUnknown_NormalizesToNull()
    {
        var builder = new LocalChatRuntimePackageBuilder();

        var package = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1,
            ReasoningEffort: "bogus"));

        AssertEx.Null(package.ReasoningEffort);
    }

    [Test]
    public void Build_WhenSamplingOptionsProvided_PassesThroughOntoRuntimePackage()
    {
        var builder = new LocalChatRuntimePackageBuilder();
        var sampling = new SamplingOptions
        {
            Temperature = 0.4f,
            TopP = 0.9f,
            MinP = 0.05f,
            NumCtx = 8192,
            Stop = ["END"]
        };

        var package = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1,
            SamplingOptions: sampling));

        var carried = AssertEx.NotNull(package.SamplingOptions);
        AssertEx.Equal(expected: 0.4f, carried.Temperature);
        AssertEx.Equal(expected: 0.9f, carried.TopP);
        AssertEx.Equal(expected: 0.05f, carried.MinP);
        AssertEx.Equal(expected: 8192, carried.NumCtx);
        AssertEx.Equal("END", AssertEx.NotNull(carried.Stop)[0]);
    }

    // Invariant guard (mirrors SupportsThinking): sampling overrides must NOT enter the config hash, so a send with
    // sampling produces a byte-identical ConfigHash to the same send without it. RuntimePackageConfigHash.Compute has
    // no sampling parameter (structural guarantee); this proves the builder also keeps it out of the digest.
    [Test]
    public void Build_WhenSamplingOptionsProvided_LeavesConfigHashByteIdentical()
    {
        var builder = new LocalChatRuntimePackageBuilder();
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var withoutSampling = builder.Build(new LocalChatRuntimePackageRequest(invocationId,
            conversationId,
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1,
            ReasoningEffort: "high"));

        var withSampling = builder.Build(new LocalChatRuntimePackageRequest(invocationId,
            conversationId,
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1,
            ReasoningEffort: "high",
            SamplingOptions: new SamplingOptions
            {
                Temperature = 0.4f,
                NumCtx = 8192
            }));

        AssertEx.Equal(withoutSampling.ConfigHash, withSampling.ConfigHash);
        AssertEx.Null(withoutSampling.SamplingOptions);
        AssertEx.NotNull(withSampling.SamplingOptions);
    }

    // Invariant guard (mirrors SupportsThinking/SamplingOptions above): the unattended flag is an execution-context bit,
    // not agent configuration, so a scheduled run must hash byte-identically to the same agent run interactively.
    [Test]
    public void Build_WhenUnattended_LeavesConfigHashByteIdentical()
    {
        var builder = new LocalChatRuntimePackageBuilder();
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var interactive = builder.Build(new LocalChatRuntimePackageRequest(invocationId,
            conversationId,
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1));

        var unattended = builder.Build(new LocalChatRuntimePackageRequest(invocationId,
            conversationId,
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1,
            IsUnattended: true));

        AssertEx.Equal(interactive.ConfigHash, unattended.ConfigHash);
        AssertEx.False(interactive.IsUnattended);
        AssertEx.True(unattended.IsUnattended);
    }

    // The §5 byte-identity guard for the tool-relevance opt-out (same posture as IsUnattended above): the filter narrows
    // only the array handed to the provider, never the offer, the resolved prompt or the approval wrap, so an agent that
    // opts out must hash byte-identically to the same agent that does not — and toggling it can never invalidate a resume.
    [Test]
    public void Build_WithToolRelevanceOptOut_ProducesTheSameConfigHash()
    {
        var builder = new LocalChatRuntimePackageBuilder();
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var filtered = builder.Build(new LocalChatRuntimePackageRequest(invocationId,
            conversationId,
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1));

        var optedOut = builder.Build(new LocalChatRuntimePackageRequest(invocationId,
            conversationId,
            "You are helpful.",
            [CreateMessage(MessageRole.User, "hello", sortOrder: 0)],
            "qwen3.5:0.8b",
            AgentDefinitionVersion: 1,
            DisableToolRelevanceFilter: true));

        AssertEx.Equal(filtered.ConfigHash, optedOut.ConfigHash);
        AssertEx.False(filtered.DisableToolRelevanceFilter);
        AssertEx.True(optedOut.DisableToolRelevanceFilter);
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
