namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AzureFoundryChatClientFactoryTests
{
    [Test]
    public void Create_WhenConnectionIsValid_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(CreateConnection(), "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenEndpointIsBlank_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(endpoint: " "), "gpt-4o"));
    }

    [Test]
    public void Create_WhenApiKeyModeMissingKey_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(apiKey: " "), "gpt-4o"));
    }

    [Test]
    public void Create_WhenDeploymentNameIsBlank_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(), " "));
    }

    [Test]
    public void Create_WhenHostNotAllowlisted_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(endpoint: "https://evil.example.com/"), "gpt-4o"));
    }

    [Test]
    public void Create_WhenManagedIdentityMode_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ManagedIdentity,
            ApiKey = null,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        }, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenHeadersPresent_ApiKey_BuildsClient()
    {
        var factory = new AzureFoundryChatClientFactory();
        var connection = CreateConnection() with
        {
            Headers =
            [
                new StoredAzureFoundryHeader
                {
                    Name = "Ocp-Apim-Subscription-Key",
                    Value = "sub",
                    IsSecret = true
                }
            ]
        };

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenHeadersPresent_ManagedIdentity_BuildsClient()
    {
        var factory = new AzureFoundryChatClientFactory();
        var connection = new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.services.ai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ManagedIdentity,
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
                    Name = "X-Tenant",
                    Value = "tenant-a",
                    IsSecret = false
                }
            ]
        };

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_ManagedIdentity_CustomHost_AllowedWhenSuffixPresent()
    {
        var factory = new AzureFoundryChatClientFactory();
        var connection = new StoredAzureFoundryConnection
        {
            Endpoint = "https://gateway.azure-api.net/",
            AuthMode = AzureFoundryAuthMode.ManagedIdentity,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ],
            AdditionalAllowedHostSuffixes = [".azure-api.net"]
        };

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_CustomHost_WithoutSuffix_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(endpoint: "https://gateway.azure-api.net/"), "gpt-4o"));
    }

    private static void ThrowsConfig(Action action)
    {
        try
        {
            action();
        }
        catch (AzureFoundryProviderException exception)
        {
            AssertEx.True(exception.Kind == AzureFoundryProviderErrorKind.Configuration);
            return;
        }

        throw new AssertionException($"Expected {nameof(AzureFoundryProviderException)} of kind {nameof(AzureFoundryProviderErrorKind.Configuration)}.");
    }

    private static StoredAzureFoundryConnection CreateConnection(string endpoint = "https://example.openai.azure.com/",
        string? apiKey = "test-api-key")
    {
        return new StoredAzureFoundryConnection
        {
            Endpoint = endpoint,
            AuthMode = AzureFoundryAuthMode.ApiKey,
            ApiKey = apiKey,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        };
    }
}
