namespace XE_Local_AI_Engine.Tests.NodeSettings;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeSettingsEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetNodeSettings_ReturnsStoredSettings()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings { MaxMessageRequestTimeoutSeconds = 120 });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/node-settings");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(120, settings.MaxMessageRequestTimeoutSeconds);
        AssertEx.Equal(StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, settings.MinMessageRequestTimeoutSeconds);
        AssertEx.Equal(StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds, settings.MaxAllowedMessageRequestTimeoutSeconds);
        await nodeSettingsStore.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenValid_SavesAndReportsCapabilities()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(nodeSettingsStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest { MaxMessageRequestTimeoutSeconds = 600 });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var settings = await ReadJsonAsync<NodeSettingsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(600, settings.MaxMessageRequestTimeoutSeconds);
        await nodeSettingsStore.Received(1).SaveAsync(
            Arg.Is<StoredNodeSettings>(stored => stored.MaxMessageRequestTimeoutSeconds == 600),
            Arg.Any<CancellationToken>());
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveNodeSettings_WhenOutOfRange_ReturnsValidationProblem()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(nodeSettingsStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/node-settings");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest { MaxMessageRequestTimeoutSeconds = 1 });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
        await capabilityReporter.DidNotReceiveWithAnyArgs().ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    private static TestingWebAppFactory CreateFactory(INodeSettingsStore nodeSettingsStore, ICapabilityReporter? capabilityReporter = null)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton(nodeSettingsStore);
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

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}
