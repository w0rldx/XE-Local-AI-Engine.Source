namespace XE_Local_AI_Engine.AI.Agent.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Configuration.Validation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Branch coverage for the three agent-configuration validators that had none. Each is registered with
///     <c>ValidateOnStart</c>, so every branch here is the difference between a misconfiguration that stops the host at
///     boot and one that surfaces much later as an agent that will not construct.
/// </summary>
public sealed class AgentOptionsValidatorTests
{
    [Test]
    public void OrchestrationValidator_WithDefaults_ReturnsSuccess()
    {
        var result = new OrchestrationAgentOptionsValidator().Validate(name: null, new OrchestrationAgentOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void OrchestrationValidator_WhenIdleTimeoutIsNotPositive_ReturnsFailure(int seconds)
    {
        // A non-positive idle timeout would expire every orchestration the instant it started.
        var result = new OrchestrationAgentOptionsValidator().Validate(name: null, new OrchestrationAgentOptions
        {
            IdleTimeoutSeconds = seconds
        });

        AssertFailureContains(result, "Agent:Orchestration:IdleTimeoutSeconds must be positive.");
    }

    [Test]
    public void InvocationValidator_WithDefaults_ReturnsSuccess()
    {
        var result = new InvocationAgentOptionsValidator().Validate(name: null, new InvocationAgentOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void InvocationValidator_WhenTheAgentNamePrefixIsBlank_ReturnsFailure(string prefix)
    {
        var result = new InvocationAgentOptionsValidator().Validate(name: null, new InvocationAgentOptions
        {
            AgentNamePrefix = prefix
        });

        AssertFailureContains(result, "Agent:Invocation:AgentNamePrefix is required.");
    }

    [Test]
    public void LocalChatValidator_WithDefaults_ReturnsSuccess()
    {
        var result = new LocalChatAgentOptionsValidator().Validate(name: null, new LocalChatAgentOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void LocalChatValidator_WhenTheAgentNameIsBlank_ReturnsFailure(string value)
    {
        AssertFailureContains(ValidateLocalChat(options => options.AgentName = value), "Agent:LocalChat:AgentName is required.");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void LocalChatValidator_WhenTheDefaultModelIsBlank_ReturnsFailure(string value)
    {
        AssertFailureContains(ValidateLocalChat(options => options.DefaultModel = value), "Agent:LocalChat:DefaultModel is required.");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void LocalChatValidator_WhenTheInstructionsResourceIsBlank_ReturnsFailure(string value)
    {
        AssertFailureContains(ValidateLocalChat(options => options.InstructionsResource = value),
            "Agent:LocalChat:InstructionsResource is required.");
    }

    [Test]
    public void LocalChatValidator_ReportsEveryMissingRequiredValueAtOnce()
    {
        var result = new LocalChatAgentOptionsValidator().Validate(name: null, new LocalChatAgentOptions
        {
            AgentName = string.Empty,
            DefaultModel = string.Empty,
            InstructionsResource = string.Empty
        });

        AssertEx.True(result.Failed);
        AssertEx.Equal(expected: 3, result.Failures!.Count());
    }

    private static ValidateOptionsResult ValidateLocalChat(Action<LocalChatAgentOptions> mutate)
    {
        var options = new LocalChatAgentOptions();
        mutate(options);
        return new LocalChatAgentOptionsValidator().Validate(name: null, options);
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.True(result.Failed);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}
