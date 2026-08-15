namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Branch coverage for the cloud-provider startup validation. The Azure conditions only fire when the provider is
///     AzureFoundry, so each one is asserted both ways: it rejects a half-configured Azure deployment, and it stays
///     silent when the node is local-only — otherwise a default (provider-none) install would fail to start.
/// </summary>
public sealed class CloudProviderOptionsValidatorTests
{
    private readonly CloudProviderOptionsValidator _validator = new();

    [Test]
    [Arguments(CloudProviderOptions.ProviderNone)]
    [Arguments("none")]
    [Arguments(CloudProviderOptions.ProviderCodexOAuth)]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WithAKnownNonAzureProvider_ReturnsSuccess(string providerName)
    {
        // Blank means "None": a node with no CloudProvider section at all must start.
        var result = _validator.Validate(name: null, new CloudProviderOptions
        {
            ProviderName = providerName
        });

        AssertEx.False(result.Failed);
    }

    [Test]
    public void Validate_WhenTheProviderIsUnknown_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, new CloudProviderOptions
        {
            ProviderName = "OpenAI"
        });

        AssertFailureContains(result, "ProviderName must be");
    }

    [Test]
    public void Validate_WithAFullyConfiguredAzureFoundryProvider_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, CreateValidAzureOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not-a-uri")]
    [Arguments("http://foundry.example.com")]
    public void Validate_WhenTheAzureEndpointIsNotAbsoluteHttps_ReturnsFailure(string? endpoint)
    {
        var options = CreateValidAzureOptions();
        options.AzureEndpoint = endpoint;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "AzureEndpoint must be an absolute HTTPS URL");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenTheAzureApiKeyIsMissing_ReturnsFailure(string? apiKey)
    {
        var options = CreateValidAzureOptions();
        options.AzureApiKey = apiKey;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "AzureApiKey is required");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenTheAzureDeploymentNameIsMissing_ReturnsFailure(string? deploymentName)
    {
        var options = CreateValidAzureOptions();
        options.AzureDeploymentName = deploymentName;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "AzureDeploymentName is required");
    }

    [Test]
    public void Validate_WhenTheProviderIsNotAzure_IgnoresTheEmptyAzureSettings()
    {
        // The whole point of the conditional branches: a local-only node has no Azure endpoint or key and must start.
        var result = _validator.Validate(name: null, new CloudProviderOptions
        {
            ProviderName = CloudProviderOptions.ProviderNone,
            AzureEndpoint = null,
            AzureApiKey = null,
            AzureDeploymentName = null
        });

        AssertEx.False(result.Failed);
    }

    private static CloudProviderOptions CreateValidAzureOptions() =>
        new()
        {
            ProviderName = CloudProviderOptions.ProviderAzureFoundry,
            AzureEndpoint = "https://foundry.example.com",
            AzureApiKey = "test-key",
            AzureDeploymentName = "gpt-test"
        };

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.True(result.Failed);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}
