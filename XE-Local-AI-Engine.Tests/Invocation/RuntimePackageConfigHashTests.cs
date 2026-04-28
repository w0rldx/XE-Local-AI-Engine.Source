namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimePackageConfigHashTests
{
    [Test]
    public void Compute_WhenUsingSharedVector_ReturnsExpectedDigest()
    {
        var canonicalJson = RuntimePackageConfigHash.SerializeCanonicalJson(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}"
                }
            ],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            });

        var digest = RuntimePackageConfigHash.Compute(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}"
                }
            ],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            });

        AssertEx.Equal(
            "{\"agentDefinitionVersion\":7,\"resolvedSystemPrompt\":\"You are a helpful local AI assistant.\",\"allowedTools\":[{\"name\":\"open_url\",\"description\":\"Open a URL in the worker browser\",\"schema\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"url\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"url\\\"]}\"}],\"modelProfile\":null,\"reasoningEffort\":null,\"timeouts\":{\"invocationTimeoutSeconds\":300,\"toolCallTimeoutSeconds\":60,\"streamIdleTimeoutSeconds\":30}}",
            canonicalJson);
        AssertEx.Equal("2725e653bfe20850855168e5a38e4a4256a0db640dc0b6453da185f4f08859d6", digest);
    }

    [Test]
    public void Compute_WhenReasoningEffortChanges_ChangesDigest()
    {
        var firstDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings(),
            "low");

        var secondDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings(),
            "high");

        AssertEx.NotEqual(firstDigest, secondDigest);
    }

    [Test]
    public void Compute_WhenAllowedToolOrderChanges_ChangesDigest()
    {
        var firstDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool_a",
                    Description = "A",
                    Schema = "{}"
                },
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool_b",
                    Description = "B",
                    Schema = "{}"
                }
            ],
            null,
            new TimeoutSettings());

        var secondDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool_b",
                    Description = "B",
                    Schema = "{}"
                },
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool_a",
                    Description = "A",
                    Schema = "{}"
                }
            ],
            null,
            new TimeoutSettings());

        AssertEx.NotEqual(firstDigest, secondDigest);
    }

    [Test]
    public void Compute_WhenAgentDefinitionVersionChanges_ChangesDigest()
    {
        var firstDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings());

        var secondDigest = RuntimePackageConfigHash.Compute(8,
            "prompt",
            [],
            null,
            new TimeoutSettings());

        AssertEx.NotEqual(firstDigest, secondDigest);
    }
}
