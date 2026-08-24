namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Which of the budget's two fixed terminal messages a trip carries. The distinction is load-bearing well past this
///     class: the work-session supervisor keeps the session running on either, and the chat pane renders the step
///     message as a neutral notice instead of the red "Response failed" alert — both by matching the constants
///     verbatim.
/// </summary>
public sealed class ProviderCallBudgetTests
{
    [Test]
    public void RegisterProviderRound_WhenTheStepCapTrips_ReportsTheStepMessage()
    {
        using var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 1);
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = 50
        });
        var budget = ProviderCallBudget.Current!;

        budget.RegisterProviderRound(estimatedInputTokens: 10);
        var exceeded = AssertEx.Throws<ProviderCallBudgetExceededException>(() => budget.RegisterProviderRound(estimatedInputTokens: 10));

        AssertEx.Equal(ProviderCallBudget.StepCallCapReachedMessage,
            exceeded.Message,
            "A caller-tightened per-step cap is a bound being spent, not a runaway loop.");
    }

    [Test]
    public void RegisterProviderRound_WhenTheInvocationCeilingTrips_ReportsTheRunawayMessage()
    {
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = 1
        });
        var budget = ProviderCallBudget.Current!;

        budget.RegisterProviderRound(estimatedInputTokens: 10);
        var exceeded = AssertEx.Throws<ProviderCallBudgetExceededException>(() => budget.RegisterProviderRound(estimatedInputTokens: 10));

        AssertEx.Equal(ProviderCallBudget.CeilingExceededMessage, exceeded.Message, "The node-wide ceiling still reads as a runaway loop.");
    }

    [Test]
    public void RegisterProviderRound_WhenTheTokenCeilingTripsUnderAStepCap_StillReportsTheRunawayMessage()
    {
        using var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 5);
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = 50,
            MaxCumulativeInputTokens = 1_024
        });
        var budget = ProviderCallBudget.Current!;

        var exceeded = AssertEx.Throws<ProviderCallBudgetExceededException>(() => budget.RegisterProviderRound(estimatedInputTokens: 2_000));

        AssertEx.Equal(ProviderCallBudget.CeilingExceededMessage,
            exceeded.Message,
            "A cumulative input-token trip is never routine, step cap or not.");
    }
}
