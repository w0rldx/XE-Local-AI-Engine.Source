namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AzureFoundryChatClientFactoryTests
{
    [Test]
    public void Create_WhenCredentialsAreValid_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(new StoredCloudCredentials
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            ApiKey = "test-api-key",
            DeploymentName = "gpt-4o"
        });

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenEndpointIsBlank_ThrowsArgumentException()
    {
        var factory = new AzureFoundryChatClientFactory();

        Throws<ArgumentException>(() => factory.Create(CreateCredentials(" ")));
    }

    [Test]
    public void Create_WhenApiKeyIsBlank_ThrowsArgumentException()
    {
        var factory = new AzureFoundryChatClientFactory();

        Throws<ArgumentException>(() => factory.Create(CreateCredentials(apiKey: " ")));
    }

    [Test]
    public void Create_WhenDeploymentNameIsBlank_ThrowsArgumentException()
    {
        var factory = new AzureFoundryChatClientFactory();

        Throws<ArgumentException>(() => factory.Create(CreateCredentials(deploymentName: " ")));
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new AssertionException($"Expected exception of type {typeof(TException).Name}.");
    }

    private static StoredCloudCredentials CreateCredentials(string endpoint = "https://example.openai.azure.com/", string apiKey = "test-api-key", string deploymentName = "gpt-4o")
    {
        return new StoredCloudCredentials
        {
            ProviderName = "AzureFoundry",
            Endpoint = endpoint,
            ApiKey = apiKey,
            DeploymentName = deploymentName
        };
    }
}
