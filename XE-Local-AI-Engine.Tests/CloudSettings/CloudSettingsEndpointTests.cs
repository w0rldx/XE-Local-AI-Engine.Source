namespace XE_Local_AI_Engine.Tests.CloudSettings;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class CloudSettingsEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetCloudSettings_WhenCredentialsExist_ReturnsSecretSafeMetadata()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(CreateCredentials());
        await using var factory = CreateFactory(cloudCredentialStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/cloud-settings");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var settings = Deserialize<CloudSettingsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("AzureFoundry", settings.ProviderName);
        AssertEx.Equal("https://example.openai.azure.com/", settings.Endpoint);
        AssertEx.Equal("gpt-4o", settings.DeploymentName);
        AssertEx.True(settings.HasStoredApiKey);
        AssertEx.False(body.Contains("test-api-key", StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveCloudSettings_WhenValid_SavesReportsAndDoesNotReturnApiKey()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            ApiKey = "new-secret-api-key",
            DeploymentName = "gpt-4o-mini"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var settings = Deserialize<CloudSettingsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("gpt-4o-mini", settings.DeploymentName);
        AssertEx.True(settings.HasStoredApiKey);
        AssertEx.False(body.Contains("new-secret-api-key", StringComparison.Ordinal));
        await cloudCredentialStore.Received(1).SaveAsync(
            Arg.Is<StoredCloudCredentials>(credentials => credentials.ApiKey == "new-secret-api-key" && credentials.DeploymentName == "gpt-4o-mini"),
            Arg.Any<CancellationToken>());
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenEndpointIsNotHttps_ReturnsValidationProblem()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "http://example.openai.azure.com/",
            ApiKey = "secret",
            DeploymentName = "gpt-4o"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await cloudCredentialStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredCloudCredentials>(), Arg.Any<CancellationToken>());
        await capabilityReporter.DidNotReceiveWithAnyArgs().ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearCloudSettings_ClearsCredentialsAndReportsCapabilities()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, "/api/local/v1/cloud-settings");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var settings = await ReadJsonAsync<CloudSettingsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(settings.HasStoredApiKey);
        AssertEx.Equal("None", settings.ProviderName);
        await cloudCredentialStore.Received(1).ClearAsync(Arg.Any<CancellationToken>());
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    private static TestingWebAppFactory CreateFactory(ICloudCredentialStore cloudCredentialStore, ICapabilityReporter? capabilityReporter = null)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ICloudCredentialStore>();
                services.AddSingleton(cloudCredentialStore);
                services.RemoveAll<ICapabilityReporter>();
                services.AddSingleton(capabilityReporter ?? Substitute.For<ICapabilityReporter>());
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        var token = factory.Services.GetRequiredService<ILocalOperatorTokenProvider>().Token;
        request.Headers.Add(LocalOperatorAuthorization.HeaderName, token);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static StoredCloudCredentials CreateCredentials()
    {
        return new StoredCloudCredentials
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            ApiKey = "test-api-key",
            DeploymentName = "gpt-4o"
        };
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(json, JsonOptions));
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}
