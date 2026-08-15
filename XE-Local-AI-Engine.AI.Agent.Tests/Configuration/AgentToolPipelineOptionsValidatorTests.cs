namespace XE_Local_AI_Engine.AI.Agent.Tests.Configuration;

using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Configuration.Validation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentToolPipelineOptionsValidatorTests
{
    private readonly AgentToolPipelineOptionsValidator _validator = new();

    [Test]
    public void Defaults_PinCurrentBehavior()
    {
        var options = new AgentToolPipelineOptions();

        AssertEx.Equal(expected: 40, options.MaximumToolIterationsPerRequest);
        AssertEx.Equal(expected: 65_536, options.MaxToolResultCharacters);
    }

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new AgentToolPipelineOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    public void Validate_WhenIterationCapNotPositive_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, new AgentToolPipelineOptions
        {
            MaximumToolIterationsPerRequest = 0
        });

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaximumToolIterationsPerRequest", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenResultBudgetBelowFloor_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, new AgentToolPipelineOptions
        {
            MaxToolResultCharacters = 512
        });

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaxToolResultCharacters", StringComparison.Ordinal));
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenConsecutiveInvalidToolCallCapNotPositive_ReturnsFailure(int value)
    {
        // Zero would trip the circuit breaker before a model had made a single mistake.
        var result = _validator.Validate(name: null, new AgentToolPipelineOptions
        {
            MaxConsecutiveInvalidToolCallsPerTool = value
        });

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaxConsecutiveInvalidToolCallsPerTool", StringComparison.Ordinal));
    }

}
