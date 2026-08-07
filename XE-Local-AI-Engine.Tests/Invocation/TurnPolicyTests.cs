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
                                           .WithSamplingOptions(new SamplingOptions
                                           {
                                               NumCtx = 4096,
                                               MaxOutputTokens = 2048
                                           })
                                           .Build();
        var budgetOptions = new ConversationContextBudgetOptions
        {
            DefaultContextTokens = 8192,
            ReservedOutputTokenFloor = 1024
        };

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
        var budgetOptions = new ConversationContextBudgetOptions
        {
            DefaultContextTokens = 6000,
            ReservedOutputTokenFloor = 500
        };

        var policy = TurnPolicy.Resolve(package,
            budgetOptions,
            new ProviderResilienceOptions(),
            new AgentToolPipelineOptions(),
            TimeSpan.FromMinutes(5));

        AssertEx.Equal(expected: 6000, policy.ContextCapacityTokens);
        AssertEx.Equal(expected: 500, policy.ReservedOutputTokens);
    }

    [Test]
    public void WithEffectiveContext_WhenLaunchedWindowExceedsTheDefault_RaisesCapacityToTheRealWindow()
    {
        // The regression: a model launched with a 64k window was pinned to the 8k default, so a 7k conversation failed
        // with ContextBudgetExceededException while the process had room for eight times as much.
        var policy = ResolveWithDefaults(new ConversationContextBudgetOptions { DefaultContextTokens = 8192 });

        var folded = policy.WithEffectiveContext(effectiveContextTokens: 65536);

        AssertEx.Equal(expected: 65536, folded.ContextCapacityTokens);
    }

    [Test]
    public void WithEffectiveContext_WhenLaunchedWindowIsBelowTheDefault_LowersCapacityToTheRealWindow()
    {
        var policy = ResolveWithDefaults(new ConversationContextBudgetOptions { DefaultContextTokens = 8192 });

        var folded = policy.WithEffectiveContext(effectiveContextTokens: 2048);

        AssertEx.Equal(expected: 2048, folded.ContextCapacityTokens);
    }

    [Test]
    public void WithEffectiveContext_WhenOverrideIsSmallerThanTheLaunchedWindow_KeepsTheOverride()
    {
        var policy = ResolveWithNumCtxOverride(numCtx: 4096);

        var folded = policy.WithEffectiveContext(effectiveContextTokens: 65536);

        AssertEx.Equal(expected: 4096, folded.ContextCapacityTokens);
    }

    [Test]
    public void WithEffectiveContext_WhenOverrideExceedsTheLaunchedWindow_ClampsToTheLaunchedWindow()
    {
        var policy = ResolveWithNumCtxOverride(numCtx: 131072);

        var folded = policy.WithEffectiveContext(effectiveContextTokens: 65536);

        AssertEx.Equal(expected: 65536, folded.ContextCapacityTokens);
    }

    [Test]
    public void WithEffectiveContext_WhenTheLaunchedWindowIsUnknown_LeavesThePolicyUnchanged()
    {
        // Cloud, Ollama, or a failed read: nothing better than the configured default is known.
        var policy = ResolveWithDefaults(new ConversationContextBudgetOptions { DefaultContextTokens = 8192 });

        AssertEx.Equal(expected: 8192, policy.WithEffectiveContext(effectiveContextTokens: null).ContextCapacityTokens);
        AssertEx.Equal(expected: 8192, policy.WithEffectiveContext(effectiveContextTokens: 0).ContextCapacityTokens);
    }

    [Test]
    public void WithEffectiveContext_WhenReservedOutputExceedsTheLaunchedWindow_ClampsItDown()
    {
        var policy = ResolveWithDefaults(new ConversationContextBudgetOptions
        {
            DefaultContextTokens = 8192,
            ReservedOutputTokenFloor = 4096
        });

        var folded = policy.WithEffectiveContext(effectiveContextTokens: 2048);

        AssertEx.Equal(expected: 2048, folded.ReservedOutputTokens);
    }

    private static TurnPolicy ResolveWithDefaults(ConversationContextBudgetOptions budgetOptions)
    {
        return TurnPolicy.Resolve(RuntimePackageBuilder.Valid().Build(),
            budgetOptions,
            new ProviderResilienceOptions(),
            new AgentToolPipelineOptions(),
            TimeSpan.FromMinutes(5));
    }

    private static TurnPolicy ResolveWithNumCtxOverride(int numCtx)
    {
        var package = RuntimePackageBuilder.Valid()
                                           .WithSamplingOptions(new SamplingOptions { NumCtx = numCtx })
                                           .Build();

        return TurnPolicy.Resolve(package,
            new ConversationContextBudgetOptions { DefaultContextTokens = 8192 },
            new ProviderResilienceOptions(),
            new AgentToolPipelineOptions(),
            TimeSpan.FromMinutes(5));
    }
}
