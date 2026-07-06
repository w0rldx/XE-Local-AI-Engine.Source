namespace XE_Local_AI_Engine.Tests.CloudProviders;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the redacting <c>PrintMembers</c> on both records (Locked #11, HIGH-2): a secret header value and the API
///     key never appear in <c>ToString</c>, while a non-secret header value still round-trips.
/// </summary>
public sealed class StoredAzureFoundryConnectionToStringTests
{
    [Test]
    public void ToString_RedactsSecretHeaderValuesAndApiKey()
    {
        var connection = new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ApiKey,
            ApiKey = "super-secret-api-key",
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ],
            Headers =
            [
                new StoredAzureFoundryHeader
                {
                    Name = "Ocp-Apim-Subscription-Key",
                    Value = "secret-header-value",
                    IsSecret = true
                },
                new StoredAzureFoundryHeader
                {
                    Name = "X-Tenant",
                    Value = "tenant-a",
                    IsSecret = false
                }
            ]
        };

        var text = connection.ToString();

        AssertEx.False(text.Contains("super-secret-api-key", StringComparison.Ordinal), "API key must not appear in ToString");
        AssertEx.False(text.Contains("secret-header-value", StringComparison.Ordinal), "secret header value must not appear in ToString");
        AssertEx.Contains(text, "[REDACTED]");
        AssertEx.Contains(text, "tenant-a");
    }

    [Test]
    public void HeaderToString_RedactsSecretValue()
    {
        var header = new StoredAzureFoundryHeader
        {
            Name = "X-Secret",
            Value = "do-not-leak",
            IsSecret = true
        };

        var text = header.ToString();

        AssertEx.False(text.Contains("do-not-leak", StringComparison.Ordinal));
        AssertEx.Contains(text, "[REDACTED]");
    }
}
