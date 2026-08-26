namespace XE_Local_AI_Engine.Tests.CloudProviders;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the effective host allowlist (built-in Azure suffixes ∪ shape-valid operator suffixes) and the operator
///     suffix shape guard: a bare TLD, a wildcard, or a suffix with no leading dot never widens the list.
/// </summary>
public sealed class AzureFoundryEndpointsHostTests
{
    [Test]
    public void IsAllowedHost_WithOperatorSuffix_AcceptsApimHost()
    {
        AssertEx.True(AzureFoundryEndpoints.IsAllowedHost(new Uri("https://gateway.azure-api.net/"), [".azure-api.net"]));
        AssertEx.True(AzureFoundryEndpoints.IsAllowedHost(new Uri("https://api.contoso.com/"), [".contoso.com"]));
    }

    [Test]
    public void IsAllowedHost_BuiltInAzureSuffix_AcceptedWithoutOperatorSuffix()
    {
        AssertEx.True(AzureFoundryEndpoints.IsAllowedHost(new Uri("https://x.openai.azure.com/")));
        AssertEx.True(AzureFoundryEndpoints.IsAllowedHost(new Uri("https://x.services.ai.azure.com/"), []));
    }

    [Test]
    public void IsAllowedHost_NonAzureHost_RejectedWithoutMatchingSuffix()
    {
        AssertEx.False(AzureFoundryEndpoints.IsAllowedHost(new Uri("https://evil.example.com/"), []));
        // A malformed operator suffix (bare TLD) must never widen the allowlist.
        AssertEx.False(AzureFoundryEndpoints.IsAllowedHost(new Uri("https://evil.example.com/"), [".com"]));
    }

    [Test]
    public void ValidateHostSuffix_RejectsBareTldWildcardAndMalformed()
    {
        AssertEx.True(AzureFoundryEndpoints.ValidateHostSuffix(".azure-api.net"));
        AssertEx.True(AzureFoundryEndpoints.ValidateHostSuffix(".contoso.com"));

        AssertEx.False(AzureFoundryEndpoints.ValidateHostSuffix(".com"));
        AssertEx.False(AzureFoundryEndpoints.ValidateHostSuffix(".net"));
        AssertEx.False(AzureFoundryEndpoints.ValidateHostSuffix("azure-api.net"));
        AssertEx.False(AzureFoundryEndpoints.ValidateHostSuffix(".*.net"));
        AssertEx.False(AzureFoundryEndpoints.ValidateHostSuffix(".foo..bar"));
        AssertEx.False(AzureFoundryEndpoints.ValidateHostSuffix(""));
        AssertEx.False(AzureFoundryEndpoints.ValidateHostSuffix(".-bad.com"));
    }
}
