namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class CentralPlatformOptionsValidatorTests
{
    private readonly CentralPlatformOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, CreateValidOptions());

        AssertEx.False(result.Failed);
        AssertEx.True(result.Failures is null || !result.Failures.Any());
    }

    [Test]
    public void Validate_WhenBaseUrlIsMissing_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.BaseUrl = string.Empty;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "BaseUrl");
    }

    [Test]
    public void Validate_WhenHeartbeatBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.HeartbeatIntervalSeconds = 4;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "HeartbeatIntervalSeconds");
    }

    [Test]
    public void Validate_WhenHeartbeatAboveMaximum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.HeartbeatIntervalSeconds = 301;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "HeartbeatIntervalSeconds");
    }

    [Test]
    public void Validate_WhenReconnectBackoffMaxIsLessThanBase_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.ReconnectBackoffBaseMs = 2000;
        options.ReconnectBackoffMaxMs = 1000;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "ReconnectBackoffMaxMs");
    }

    [Test]
    public void Validate_WhenReconnectBackoffMaxExceedsLimit_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.ReconnectBackoffMaxMs = 1800001;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "ReconnectBackoffMaxMs");
    }

    [Test]
    public void Validate_WhenReconnectMaxAttemptsExceedsLimit_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.ReconnectMaxAttempts = 101;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "ReconnectMaxAttempts");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenHubPathIsMissing_ReturnsFailure(string value)
    {
        var options = CreateValidOptions();
        options.HubPath = value;

        AssertFailureContains(_validator.Validate(name: null, options), "HubPath is required.");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenPairingEndpointIsMissing_ReturnsFailure(string value)
    {
        var options = CreateValidOptions();
        options.PairingEndpoint = value;

        AssertFailureContains(_validator.Validate(name: null, options), "PairingEndpoint is required.");
    }

    [Test]
    [Arguments("api/v1/client-nodes/pair")]
    [Arguments("https://elsewhere.example.com/pair")]
    public void Validate_WhenPairingEndpointIsNotAnApplicationPath_ReturnsFailure(string value)
    {
        // An absolute URL here would silently send the pairing request to a different origin than BaseUrl.
        var options = CreateValidOptions();
        options.PairingEndpoint = value;

        AssertFailureContains(_validator.Validate(name: null, options), "PairingEndpoint must be an absolute application path");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenDeviceBindingStartEndpointIsMissing_ReturnsFailure(string value)
    {
        var options = CreateValidOptions();
        options.DeviceBindingStartEndpoint = value;

        AssertFailureContains(_validator.Validate(name: null, options), "DeviceBindingStartEndpoint is required.");
    }

    [Test]
    public void Validate_WhenDeviceBindingStartEndpointIsNotAnApplicationPath_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.DeviceBindingStartEndpoint = "https://elsewhere.example.com/device-bind/start";

        AssertFailureContains(_validator.Validate(name: null, options), "DeviceBindingStartEndpoint must be an absolute application path");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenDeviceBindingTokenEndpointIsMissing_ReturnsFailure(string value)
    {
        var options = CreateValidOptions();
        options.DeviceBindingTokenEndpoint = value;

        AssertFailureContains(_validator.Validate(name: null, options), "DeviceBindingTokenEndpoint is required.");
    }

    [Test]
    public void Validate_WhenDeviceBindingTokenEndpointIsNotAnApplicationPath_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.DeviceBindingTokenEndpoint = "device-bind/token";

        AssertFailureContains(_validator.Validate(name: null, options), "DeviceBindingTokenEndpoint must be an absolute application path");
    }

    [Test]
    public void Validate_WhenAReconnectDelayIsNegative_ReturnsFailure()
    {
        var options = new CentralPlatformOptions
        {
            BaseUrl = "https://test.example.com",
            ReconnectDelaysMs = [0, 1000, -1]
        };

        AssertFailureContains(_validator.Validate(name: null, options), "ReconnectDelaysMs cannot contain negative values.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(30001)]
    public void Validate_WhenReconnectBackoffBaseIsOutOfRange_ReturnsFailure(int value)
    {
        var options = CreateValidOptions();
        options.ReconnectBackoffBaseMs = value;
        options.ReconnectBackoffMaxMs = 1800000;

        AssertFailureContains(_validator.Validate(name: null, options), "ReconnectBackoffBaseMs must be between 1 and 30000.");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(10001)]
    public void Validate_WhenReconnectBackoffJitterIsOutOfRange_ReturnsFailure(int value)
    {
        var options = CreateValidOptions();
        options.ReconnectBackoffJitterMs = value;

        AssertFailureContains(_validator.Validate(name: null, options), "ReconnectBackoffJitterMs must be between 0 and 10000.");
    }

    [Test]
    [Arguments(4)]
    [Arguments(601)]
    public void Validate_WhenToolCallTimeoutIsOutOfRange_ReturnsFailure(int value)
    {
        var options = CreateValidOptions();
        options.ToolCallTimeoutSeconds = value;

        AssertFailureContains(_validator.Validate(name: null, options), "ToolCallTimeoutSeconds must be between 5 and 600.");
    }

    [Test]
    [Arguments(9)]
    [Arguments(3601)]
    public void Validate_WhenInvocationTimeoutIsOutOfRange_ReturnsFailure(int value)
    {
        var options = CreateValidOptions();
        options.InvocationTimeoutSeconds = value;

        AssertFailureContains(_validator.Validate(name: null, options), "InvocationTimeoutSeconds must be between 10 and 3600.");
    }

    private static CentralPlatformOptions CreateValidOptions()
    {
        return new CentralPlatformOptions
        {
            BaseUrl = "https://test.example.com"
        };
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}
