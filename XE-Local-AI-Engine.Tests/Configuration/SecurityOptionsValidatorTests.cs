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
        var result = _validator.Validate(name: null, CreateValidOptions());

        AssertEx.False(result.Failed);
        AssertEx.True(result.Failures is null || !result.Failures.Any());
    }

    [Test]
    public void Validate_WhenMaxSystemPromptSizeBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxSystemPromptSizeKb = 0;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxSystemPromptSizeKb");
    }

    [Test]
    public void Validate_WhenMaxMessageSizeBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxMessageSizeKb = 0;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxMessageSizeKb");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(1025)]
    public void Validate_WhenMaxSystemPromptSizeIsOutOfRange_ReturnsFailure(int value)
    {
        var options = CreateValidOptions();
        options.MaxSystemPromptSizeKb = value;

        AssertFailureContains(_validator.Validate(name: null, options), "MaxSystemPromptSizeKb must be between 1 and 1024.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1025)]
    public void Validate_WhenMaxMessageSizeIsOutOfRange_ReturnsFailure(int value)
    {
        var options = CreateValidOptions();
        options.MaxMessageSizeKb = value;

        AssertFailureContains(_validator.Validate(name: null, options), "MaxMessageSizeKb must be between 1 and 1024.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(513)]
    public void Validate_WhenMaxUploadFileSizeIsOutOfRange_ReturnsFailure(int value)
    {
        var options = CreateValidOptions();
        options.MaxUploadFileSizeMb = value;

        AssertFailureContains(_validator.Validate(name: null, options), "MaxUploadFileSizeMb must be between 1 and 512.");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenTheAllowedModelNamePatternIsMissing_ReturnsFailure(string value)
    {
        // A blank pattern is not "allow everything" — it is a gate with nothing behind it, so startup must refuse.
        var options = CreateValidOptions();
        options.AllowedModelNamePattern = value;

        AssertFailureContains(_validator.Validate(name: null, options), "AllowedModelNamePattern is required.");
    }

    [Test]
    public void Validate_WhenTheAllowedModelNamePatternIsNotAValidRegex_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.AllowedModelNamePattern = "^[a-z";

        AssertFailureContains(_validator.Validate(name: null, options), "AllowedModelNamePattern must be a valid regular expression.");
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
