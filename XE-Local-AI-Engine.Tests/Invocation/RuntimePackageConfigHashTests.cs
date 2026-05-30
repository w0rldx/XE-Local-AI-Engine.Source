namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
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
            "{\"agentDefinitionVersion\":7,\"resolvedSystemPrompt\":\"You are a helpful local AI assistant.\",\"allowedTools\":[{\"name\":\"open_url\",\"description\":\"Open a URL in the worker browser\",\"schema\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"url\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"url\\\"]}\",\"location\":0,\"requiresApproval\":false}],\"modelProfile\":null,\"reasoningEffort\":null,\"timeouts\":{\"invocationTimeoutSeconds\":300,\"toolCallTimeoutSeconds\":60,\"streamIdleTimeoutSeconds\":30}}",
            canonicalJson);
        AssertEx.Equal("a532bda9b1fbae5b0cb6982317a98450be90a5694bb91e492a552cfed4fdd4ae", digest);
    }

    // Cross-repo round-trip guard (worker half): a ClientLocal tool carrying RequiresApproval must canonicalize to
    // the SAME bytes/digest the server MixedEnvelopeConfigHashService produces for the identical fixture vector. The
    // matching server assertion lives in C0re.Tests.UnitTests MixedEnvelopeConfigHashServiceTests; if these two golden
    // strings ever diverge, every encrypted invocation fails runtime-package-config-hash-mismatch.
    [Test]
    public void Compute_WhenUsingClientLocalSharedVector_CarriesLocationAndApprovalInDigest()
    {
        var canonicalJson = RuntimePackageConfigHash.SerializeCanonicalJson(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "run_in_agent_home",
                    Description = "Run a task in the agent home workspace",
                    Schema = "{\"type\":\"object\",\"properties\":{\"goal\":{\"type\":\"string\"}},\"required\":[\"goal\"]}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = true
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
                    Name = "run_in_agent_home",
                    Description = "Run a task in the agent home workspace",
                    Schema = "{\"type\":\"object\",\"properties\":{\"goal\":{\"type\":\"string\"}},\"required\":[\"goal\"]}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = true
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
            "{\"agentDefinitionVersion\":7,\"resolvedSystemPrompt\":\"You are a helpful local AI assistant.\",\"allowedTools\":[{\"name\":\"run_in_agent_home\",\"description\":\"Run a task in the agent home workspace\",\"schema\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"goal\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"goal\\\"]}\",\"location\":1,\"requiresApproval\":true}],\"modelProfile\":null,\"reasoningEffort\":null,\"timeouts\":{\"invocationTimeoutSeconds\":300,\"toolCallTimeoutSeconds\":60,\"streamIdleTimeoutSeconds\":30}}",
            canonicalJson);
        AssertEx.Equal("58ed36f89182fe9e40d0c0d0dc4bc8cbee19e210419046e188e2af91fb077fbb", digest);
    }

    [Test]
    public void Compute_WhenToolLocationChanges_ChangesDigest()
    {
        var apiSideDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool",
                    Description = "d",
                    Schema = "{}",
                    Location = ToolLocation.ApiSide,
                    RequiresApproval = false
                }
            ],
            null,
            new TimeoutSettings());

        var clientLocalDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool",
                    Description = "d",
                    Schema = "{}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = false
                }
            ],
            null,
            new TimeoutSettings());

        AssertEx.NotEqual(apiSideDigest, clientLocalDigest);
    }

    [Test]
    public void Compute_WhenRequiresApprovalChanges_ChangesDigest()
    {
        var withoutApprovalDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool",
                    Description = "d",
                    Schema = "{}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = false
                }
            ],
            null,
            new TimeoutSettings());

        var withApprovalDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool",
                    Description = "d",
                    Schema = "{}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = true
                }
            ],
            null,
            new TimeoutSettings());

        AssertEx.NotEqual(withoutApprovalDigest, withApprovalDigest);
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
