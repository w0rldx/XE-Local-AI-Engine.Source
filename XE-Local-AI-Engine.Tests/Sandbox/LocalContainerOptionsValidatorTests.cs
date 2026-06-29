namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalContainerOptionsValidatorTests
{
    [Test]
    public void Validate_WithDefaults_Succeeds()
    {
        var result = Validate(new LocalContainerOptions());

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void Validate_WithPositiveMaxCopyFileBytes_Succeeds()
    {
        var result = Validate(new LocalContainerOptions
        {
            MaxCopyFileBytes = 1
        });

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void Validate_WithZeroMaxCopyFileBytes_Fails()
    {
        var result = Validate(new LocalContainerOptions
        {
            MaxCopyFileBytes = 0
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "MaxCopyFileBytes");
    }

    [Test]
    public void Validate_WithNegativeMaxCopyFileBytes_Fails()
    {
        var result = Validate(new LocalContainerOptions
        {
            MaxCopyFileBytes = -1
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "MaxCopyFileBytes");
    }

    private static ValidateOptionsResult Validate(LocalContainerOptions options)
    {
        return new LocalContainerOptionsValidator().Validate(Options.DefaultName, options);
    }
}
