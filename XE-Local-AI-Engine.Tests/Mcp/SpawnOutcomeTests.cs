namespace XE_Local_AI_Engine.Tests.Mcp;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SpawnOutcomeTests
{
    [Test]
    public void ToSynchronousResult_WhenOutcomeSucceeds_ReturnsContent()
    {
        var outcome = SpawnOutcome.Success("local model result");

        var result = outcome.ToSynchronousResult();

        AssertEx.Equal("local model result", result);
    }

    [Test]
    public void ToSynchronousResult_WhenOutcomeIsRejected_ReturnsSanitizedDisplayMessage()
    {
        var outcome = SpawnOutcome.Rejected("internal_failure_code", "Cannot run: the model is busy.");

        var result = outcome.ToSynchronousResult();

        AssertEx.Equal("Cannot run: the model is busy.", result);
        AssertEx.False(result.Contains("internal_failure_code", StringComparison.Ordinal),
            "the synchronous compatibility adapter must not expose the stable internal failure code.");
    }

    [Test]
    public void ToSynchronousResult_WhenOutcomeFails_ReturnsSanitizedDisplayMessage()
    {
        var outcome = SpawnOutcome.Failed("internal_failure", "The local run failed safely.");

        var result = outcome.ToSynchronousResult();

        AssertEx.Equal("The local run failed safely.", result);
    }
}
