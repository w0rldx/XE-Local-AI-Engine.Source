namespace XE_Local_AI_Engine.Tests.Configuration;

using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentHomeOptionsValidatorTests
{
    private readonly AgentHomeOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(null, new AgentHomeOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    public void Validate_WhenMaxSelectedFolderBytesNotPositive_ReturnsFailure()
    {
        var options = new AgentHomeOptions { MaxSelectedFolderBytes = 0 };

        var result = _validator.Validate(null, options);

        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaxSelectedFolderBytes", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenMaxPatchBytesNotPositive_ReturnsFailure()
    {
        var options = new AgentHomeOptions { MaxPatchBytes = 0 };

        var result = _validator.Validate(null, options);

        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaxPatchBytes", StringComparison.Ordinal));
    }
}
