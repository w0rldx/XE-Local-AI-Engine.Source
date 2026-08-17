namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentHomeOptionsValidatorTests
{
    private readonly AgentHomeOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new AgentHomeOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    public void Validate_WhenMaxSelectedFolderBytesNotPositive_ReturnsFailure()
    {
        var options = new AgentHomeOptions
        {
            MaxSelectedFolderBytes = 0
        };

        var result = _validator.Validate(name: null, options);

        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaxSelectedFolderBytes", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenMaxPatchBytesNotPositive_ReturnsFailure()
    {
        var options = new AgentHomeOptions
        {
            MaxPatchBytes = 0
        };

        var result = _validator.Validate(name: null, options);

        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaxPatchBytes", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenPatchApplyTimeoutSecondsNotPositive_ReturnsFailure()
    {
        var options = new AgentHomeOptions
        {
            PatchApplyTimeoutSeconds = 0
        };

        var result = _validator.Validate(name: null, options);

        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains("PatchApplyTimeoutSeconds", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenEnabledAndToolCapableModelsEmpty_ReturnsFailure()
    {
        var options = new AgentHomeOptions
        {
            Enabled = true,
            ToolCapableModels = []
        };

        var result = _validator.Validate(name: null, options);

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Failures, failure => failure.Contains("ToolCapableModels", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenDisabledAndToolCapableModelsEmpty_ReturnsSuccess()
    {
        // The capability allow-list is only required when AgentHome is enabled.
        var options = new AgentHomeOptions
        {
            Enabled = false,
            ToolCapableModels = []
        };

        var result = _validator.Validate(name: null, options);

        AssertEx.False(result.Failed);
    }

    [Test]
    public void Validate_WhenEnabledAndToolCapableModelsPopulated_ReturnsSuccess()
    {
        var options = new AgentHomeOptions
        {
            Enabled = true,
            ToolCapableModels = ["qwen3:8b"]
        };

        var result = _validator.Validate(name: null, options);

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenPrepareStaleAfterSecondsNotPositive_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, new AgentHomeOptions
        {
            PrepareStaleAfterSeconds = value
        });

        AssertFailureContains(result, "PrepareStaleAfterSeconds must be greater than zero.");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenRootPathIsSpecifiedButBlank_ReturnsFailure(string value)
    {
        // Null means "use the default root"; a blank string is a truncated config value, not a default.
        var result = _validator.Validate(name: null, new AgentHomeOptions
        {
            RootPath = value
        });

        AssertFailureContains(result, "RootPath must not be blank when specified.");
    }

    [Test]
    public void Validate_WhenRootPathIsNull_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new AgentHomeOptions
        {
            RootPath = null
        });

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenDefaultRuntimeProfileIsBlank_ReturnsFailure(string value)
    {
        var result = _validator.Validate(name: null, new AgentHomeOptions
        {
            DefaultRuntimeProfile = value
        });

        AssertFailureContains(result, "DefaultRuntimeProfile must not be blank.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenPrepareTimeoutSecondsNotPositive_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, new AgentHomeOptions
        {
            PrepareTimeoutSeconds = value
        });

        AssertFailureContains(result, "PrepareTimeoutSeconds must be greater than zero.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenCommandTimeoutSecondsNotPositive_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, new AgentHomeOptions
        {
            CommandTimeoutSeconds = value
        });

        AssertFailureContains(result, "CommandTimeoutSeconds must be greater than zero.");
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}
