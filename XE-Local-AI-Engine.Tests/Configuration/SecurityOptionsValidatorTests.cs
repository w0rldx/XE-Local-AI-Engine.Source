namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SecurityOptionsValidatorTests
{
    private readonly SecurityOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(null, CreateValidOptions());

        AssertEx.False(result.Failed);
        AssertEx.True(result.Failures is null || !result.Failures.Any());
    }

    [Test]
    public void Validate_WhenMaxSystemPromptSizeBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxSystemPromptSizeKb = 0;

        var result = _validator.Validate(null, options);

        AssertFailureContains(result, "MaxSystemPromptSizeKb");
    }

    [Test]
    public void Validate_WhenMaxMessageSizeBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxMessageSizeKb = 0;

        var result = _validator.Validate(null, options);

        AssertFailureContains(result, "MaxMessageSizeKb");
    }

    private static SecurityOptions CreateValidOptions()
    {
        return new SecurityOptions();
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}
