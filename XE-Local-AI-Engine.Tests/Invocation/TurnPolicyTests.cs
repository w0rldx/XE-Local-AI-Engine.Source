namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Policy;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

public sealed class TurnPolicyTests
{
    [Test]
    public void Resolve_WithDefaultOptions_CopiesEveryValueFromItsConfiguredSource()
    {
        var package = RuntimePackageBuilder.Valid()
                                            .WithTimeout(invocationSeconds: 120, toolCallSeconds: 15, streamIdleSeconds: 45)
                                            .Build();

        var policy = TurnPolicy.Resolve(package,
            new ConversationContextBudgetOptions(),
            new ProviderResilienceOptions(),
            new AgentToolPipelineOptions(),
            fallbackToolResultTimeout: TimeSpan.FromMinutes(5));

        AssertEx.Equal(TimeSpan.FromSeconds(120), policy.InvocationTimeout);
        AssertEx.Equal(TimeSpan.FromSeconds(45), policy.StreamIdleTimeout);
        AssertEx.True(policy.StreamIdleTimeoutMessage.Contains("45", StringComparison.Ordinal), "message should name the seconds that fired");
        // ToolCallTimeoutSeconds is set (15 > 0) so it wins over the fallback.
        AssertEx.Equal(TimeSpan.FromSeconds(15), policy.ToolResultTimeout);
        AssertEx.Equal(new ConversationContextBudgetOptions().DefaultContextTokens, policy.ContextCapacityTokens);
        AssertEx.Equal(new ConversationContextBudgetOptions().ReservedOutputTokenFloor, policy.ReservedOutputTokens);
        AssertEx.Equal(new AgentToolPipelineOptions().MaximumToolIterationsPerRequest, policy.MaxToolIterationsPerRequest);
        AssertEx.Equal(new AgentToolPipelineOptions().MaxConsecutiveInvalidToolCallsPerTool, policy.MaxConsecutiveInvalidToolCallsPerTool);
        AssertEx.True(policy.RetryEnabled);
        AssertEx.Equal(new ProviderResilienceOptions().MaxRetries, policy.MaxRetries);
        AssertEx.True(policy.CircuitBreakerEnabled);
    }

    [Test]
    public void Resolve_WhenToolCallTimeoutSecondsIsZero_FallsBackToNodeGlobalAge()
    {
        var package = RuntimePackageBuilder.Valid()
                                            .WithTimeout(toolCallSeconds: 0)
                                            .Build();
        var fallback = TimeSpan.FromMinutes(7);

        var policy = TurnPolicy.Resolve(package,
            new ConversationContextBudgetOptions(),
            new ProviderResilienceOptions(),
            new AgentToolPipelineOptions(),
            fallback);

        AssertEx.Equal(fallback, policy.ToolResultTimeout);
    }

    [Test]
    public void Resolve_WhenPackageHasNumCtxOverride_UsesItAsCapacity()
    {
        var package = RuntimePackageBuilder.Valid()
                                            .WithSamplingOptions(new SamplingOptions { NumCtx = 4096, MaxOutputTokens = 2048 })
                                            .Build();
        var budgetOptions = new ConversationContextBudgetOptions { DefaultContextTokens = 8192, ReservedOutputTokenFloor = 1024 };

        var policy = TurnPolicy.Resolve(package,
            budgetOptions,
            new ProviderResilienceOptions(),
            new AgentToolPipelineOptions(),
            TimeSpan.FromMinutes(5));

        AssertEx.Equal(expected: 4096, policy.ContextCapacityTokens);
        // The explicit max-output-tokens override (2048) is larger than the configured floor (1024), so it wins.
        AssertEx.Equal(expected: 2048, policy.ReservedOutputTokens);
    }

    [Test]
    public void Resolve_WhenNoNumCtxOverride_FallsBackToConfiguredDefaultContextTokens()
    {
        var package = RuntimePackageBuilder.Valid().Build();
        var budgetOptions = new ConversationContextBudgetOptions { DefaultContextTokens = 6000, ReservedOutputTokenFloor = 500 };

        var policy = TurnPolicy.Resolve(package,
            budgetOptions,
            new ProviderResilienceOptions(),
            new AgentToolPipelineOptions(),
            TimeSpan.FromMinutes(5));

        AssertEx.Equal(expected: 6000, policy.ContextCapacityTokens);
        AssertEx.Equal(expected: 500, policy.ReservedOutputTokens);
    }
}
