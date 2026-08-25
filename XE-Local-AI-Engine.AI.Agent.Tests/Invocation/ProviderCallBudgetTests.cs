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

    [Test]
    public void CaptureConsumption_WhenNoBudgetWasCreatedUnderTheScope_AnswersNothing()
    {
        using var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 10);

        AssertEx.Null(cap.CaptureConsumption(), "An empty record would read as 'this step was free', which is a different claim from 'nothing ran'.");
    }

    [Test]
    public void CaptureConsumption_AfterTheRunHasLanded_ReportsWhatItSpentAgainstTheSeededCap()
    {
        // The seam the work-session supervisor reads: the run's own ambient budget is invisible from out here, because
        // an AsyncLocal write inside the run does not flow back to its caller. The scope object does, by reference.
        using var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 10);
        RunOneTurn();

        var consumption = AssertEx.NotNull(cap.CaptureConsumption(), "A budget was created under the scope, so its counters are readable.");
        AssertEx.Equal(expected: 3, consumption.ProviderCalls);
        AssertEx.Equal(expected: 4_500L, consumption.EstimatedInputTokens);
        AssertEx.Equal(expected: 2, consumption.ToolCallsCompleted);
        AssertEx.Equal(expected: 10, consumption.ProviderCallCap, "The cap rides along so a reader can size the calls against it.");
        AssertEx.Equal(expected: 1, consumption.AttachedBudgets, "One invocation ran, so the calls ARE a ratio against the cap.");
    }

    [Test]
    public void CaptureConsumption_WhenTheCapTrips_CountsOnlyTheAdmittedRounds()
    {
        using var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 2);
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   MaxProviderCallsPerInvocation = 50
               }))
        {
            var budget = ProviderCallBudget.Current!;
            budget.RegisterProviderRound(estimatedInputTokens: 100);
            budget.RegisterProviderRound(estimatedInputTokens: 100);
            _ = AssertEx.Throws<ProviderCallBudgetExceededException>(() => budget.RegisterProviderRound(estimatedInputTokens: 100));
        }

        var consumption = AssertEx.NotNull(cap.CaptureConsumption());
        AssertEx.Equal(expected: 2, consumption.ProviderCalls, "The rejected round never reached the provider, so a spent cap reads 2/2 rather than 3/2.");
        AssertEx.Equal(expected: 200L, consumption.EstimatedInputTokens, "The rejected round's tokens were never sent either.");
    }

    [Test]
    public void CaptureConsumption_WhenTheRunSpawnedANestedInvocation_SumsBothBudgets()
    {
        // A sub-agent spawn seeds its own budget under the same cap scope. The cap applies to each separately, so the
        // sum is the honest answer to "what did this step spend" even when it runs past the per-budget ceiling.
        using var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 10);
        RunOneTurn();
        RunOneTurn();

        var consumption = AssertEx.NotNull(cap.CaptureConsumption());
        AssertEx.Equal(expected: 6, consumption.ProviderCalls);
        AssertEx.Equal(expected: 9_000L, consumption.EstimatedInputTokens);
        AssertEx.Equal(expected: 4, consumption.ToolCallsCompleted);
        AssertEx.Equal(expected: 2, consumption.AttachedBudgets, "Six calls against a cap of ten is two runs of three, not one run of six — the count is what says so.");
        AssertEx.Equal(expected: 10, consumption.ProviderCallCap, "The cap is unchanged: it bounds each budget, not their sum.");
    }

    [Test]
    public void BeginCallCapScope_WhenANestedScopeIsDisposed_RestoresTheOuterCap()
    {
        using var outer = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 3);
        using (ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 1))
        {
            // Deliberately empty: the nested scope exists only to be disposed.
        }

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   MaxProviderCallsPerInvocation = 50
               }))
        {
            var budget = ProviderCallBudget.Current!;
            budget.RegisterProviderRound(estimatedInputTokens: 1);
            budget.RegisterProviderRound(estimatedInputTokens: 1);
            budget.RegisterProviderRound(estimatedInputTokens: 1);
        }

        AssertEx.Equal(expected: 3, AssertEx.NotNull(outer.CaptureConsumption()).ProviderCalls, "The outer cap of three, not the nested cap of one, is what bounded the run.");
    }

    [Test]
    public void Attach_AfterTheScopeIsDisposed_IsIgnoredRatherThanThrowing()
    {
        // Reached for real when a run outlives the caller that seeded the scope: disposal restores the ambient value
        // for the DISPOSING flow only, so an invocation still unwinding on its own async context — which is precisely
        // how a cancelled step ends — holds the disposed scope and seeds a budget into it. Faulting that run to
        // protect a measurement nobody will read would trade a real turn for a number.
        var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 10);
        ProviderCallBudget budget;
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions()))
        {
            budget = ProviderCallBudget.Current!;
        }

        cap.Dispose();
        cap.Attach(budget);

        AssertEx.Null(cap.CaptureConsumption(), "A disposed scope collects nothing, so it reports nothing.");
    }

    [Test]
    public void Dispose_ReleasesTheCollectedBudgetsAndIsIdempotent()
    {
        var cap = ProviderCallBudget.BeginCallCapScope(maxProviderCalls: 10);
        RunOneTurn();
        AssertEx.NotNull(cap.CaptureConsumption(), "Read it BEFORE disposing — that is the contract the supervisor keeps.");

        cap.Dispose();
        cap.Dispose();

        AssertEx.Null(cap.CaptureConsumption(), "Disposal drops the budgets so a long-lived caller cannot pin one run's counters.");
    }

    /// <summary>Stands in for one invocation: the runner seeds a scope, the pipeline registers rounds and tool calls.</summary>
    private static void RunOneTurn()
    {
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = 50
        });
        var budget = ProviderCallBudget.Current!;
        budget.RegisterProviderRound(estimatedInputTokens: 1_000);
        budget.RecordToolCallCompleted(TimeSpan.FromMilliseconds(5), resultBytes: 128, failed: false);
        budget.RegisterProviderRound(estimatedInputTokens: 1_500);
        budget.RecordToolCallCompleted(TimeSpan.FromMilliseconds(5), resultBytes: 128, failed: true);
        budget.RegisterProviderRound(estimatedInputTokens: 2_000);
    }
}
