namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one relation neither section's data annotations can see: a park must expire before the node expires the
///     pending tool call it is parked on. Both sides of the boundary are asserted, so a park budget equal to the
///     tool-call age fails.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WorkSessionOptionsValidatorTests
{
    [Test]
    public void Validate_WithDefaults_ReturnsSuccess()
    {
        // 300 seconds against the node's 10-minute default: the shipped pair has to be a passing pair.
        AssertEx.False(Validate(new WorkSessionOptions(), pendingToolCallAgeMinutes: 10).Failed);
    }

    [Test]
    public void Validate_WhenTheParkBudgetReachesTheToolCallAge_ReturnsFailure()
    {
        var result = Validate(new WorkSessionOptions
        {
            MaxParkedSeconds = 600
        }, pendingToolCallAgeMinutes: 10);

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.Failures, failure => failure.Contains("WorkSessions:MaxParkedSeconds", StringComparison.Ordinal));
        AssertEx.Contains(result.Failures, failure => failure.Contains("WorkerNode:MaxPendingToolCallAgeMinutes", StringComparison.Ordinal));
        AssertEx.Contains(result.Failures, failure => failure.Contains("600", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenTheParkBudgetStaysUnderTheToolCallAge_ReturnsSuccess()
    {
        AssertEx.False(Validate(new WorkSessionOptions
        {
            MaxParkedSeconds = 599
        }, pendingToolCallAgeMinutes: 10).Failed);
    }

    private static ValidateOptionsResult Validate(WorkSessionOptions options, int pendingToolCallAgeMinutes)
    {
        var workerNode = Options.Create(new WorkerNodeOptions
        {
            NodeName = "test-node",
            MaxPendingToolCallAgeMinutes = pendingToolCallAgeMinutes
        });

        return new WorkSessionOptionsValidator(workerNode).Validate(name: null, options);
    }
}
