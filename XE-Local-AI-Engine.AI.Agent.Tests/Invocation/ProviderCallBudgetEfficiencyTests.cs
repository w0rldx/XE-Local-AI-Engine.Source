namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProviderCallBudgetEfficiencyTests
{
    [Test]
    public void CaptureEfficiencySnapshot_AggregatesOnlyNumericHarnessMeasurements()
    {
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions());
        var budget = ProviderCallBudget.Current!;

        budget.RegisterProviderRound(estimatedInputTokens: 120, toolSchemaTokens: 20, messagesDropped: 2, toolResultsTruncated: 1, charsTruncated: 300);
        budget.RegisterProviderRound(estimatedInputTokens: 80, toolSchemaTokens: 10);
        budget.RecordProviderRoundElapsed(TimeSpan.FromMilliseconds(12.5));
        budget.RecordProviderRoundElapsed(TimeSpan.FromMilliseconds(7.5));
        budget.RecordToolCallRequested("read_document");
        budget.RecordToolCallCompleted(TimeSpan.FromMilliseconds(4.25), resultBytes: 256, failed: true);
        budget.RecordProviderRetry();
        budget.RecordToolArgumentRepair();
        budget.RecordAgentHandoff();

        var snapshot = budget.CaptureEfficiencySnapshot();

        AssertEx.Equal(expected: 2, snapshot.ProviderCalls);
        AssertEx.Equal(expected: 0, snapshot.ProviderRoundsRejected);
        AssertEx.Equal(expected: 200L, snapshot.EstimatedInputTokens);
        AssertEx.Equal(expected: 120, snapshot.MaximumEstimatedInputTokens);
        AssertEx.Equal(expected: 30L, snapshot.ToolSchemaTokens);
        AssertEx.Equal(expected: 20, snapshot.MaximumToolSchemaTokens);
        AssertEx.Equal(expected: 20d, snapshot.ProviderRoundElapsedMs);
        AssertEx.Equal(expected: 2L, snapshot.MessagesDropped);
        AssertEx.Equal(expected: 1L, snapshot.ToolResultsTruncated);
        AssertEx.Equal(expected: 300L, snapshot.CharsTruncated);
        AssertEx.Equal(expected: 1, snapshot.ToolCallsRequested);
        AssertEx.Equal(expected: 1, snapshot.ToolCallsCompleted);
        AssertEx.Equal(expected: 1, snapshot.ToolCallsFailed);
        AssertEx.Equal(expected: 4.25d, snapshot.ToolRequestToResultMs);
        AssertEx.Equal(expected: 256L, snapshot.ToolResultBytes);
        AssertEx.True(snapshot.TimeToFirstToolRequestMs is >= 0);
        AssertEx.Equal(expected: 1, snapshot.ProviderRetries);
        AssertEx.Equal(expected: 1, snapshot.ToolArgumentRepairs);
        AssertEx.Equal(expected: 1, snapshot.AgentHandoffs);
    }

    [Test]
    public void CaptureEfficiencySnapshot_ExcludesProviderRoundRejectedByCeiling()
    {
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = 1
        });
        var budget = ProviderCallBudget.Current!;

        budget.RegisterProviderRound(estimatedInputTokens: 100, toolSchemaTokens: 10);
        _ = AssertEx.Throws<ProviderCallBudgetExceededException>(() =>
            budget.RegisterProviderRound(estimatedInputTokens: 900, toolSchemaTokens: 90, messagesDropped: 3));

        var snapshot = budget.CaptureEfficiencySnapshot();

        AssertEx.Equal(expected: 1, snapshot.ProviderCalls);
        AssertEx.Equal(expected: 1, snapshot.ProviderRoundsRejected);
        AssertEx.Equal(expected: 100L, snapshot.EstimatedInputTokens);
        AssertEx.Equal(expected: 10L, snapshot.ToolSchemaTokens);
        AssertEx.Equal(expected: 0L, snapshot.MessagesDropped);
    }
}
