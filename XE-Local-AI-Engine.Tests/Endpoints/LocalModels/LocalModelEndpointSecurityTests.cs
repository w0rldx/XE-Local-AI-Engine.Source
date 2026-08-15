namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalModelEndpointSecurityTests
{
    [Test]
    public async Task LocalModelEndpoints_WhenTokenMissing_AreRejected()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        await using var factory = CreateFactory(modelService);
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync("/api/local/v1/models").ConfigureAwait(false);
        using var detailsResponse = await client.GetAsync("/api/local/v1/models/llama3:8b/details").ConfigureAwait(false);
        using var selectResponse = await client.PostAsJsonAsync("/api/local/v1/models/select", new SelectLocalModelRequest
        {
            ModelName = "llama3:8b"
        }).ConfigureAwait(false);
        using var deleteResponse = await client.DeleteAsync("/api/local/v1/models/llama3:8b").ConfigureAwait(false);
        using var setKindResponse = await client.PutAsJsonAsync("/api/local/v1/models/llama3:8b/kind", new SetModelKindRequest
        {
            Kind = "Chat"
        }).ConfigureAwait(false);
        using var resetKindResponse = await client.DeleteAsync("/api/local/v1/models/llama3:8b/kind").ConfigureAwait(false);
        using var runningResponse = await client.GetAsync("/api/local/v1/models/running").ConfigureAwait(false);
        using var unloadResponse = await client.PostAsJsonAsync("/api/local/v1/models/llama3:8b/unload", new
        {
        }).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, listResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, detailsResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, selectResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, deleteResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, setKindResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, resetKindResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, runningResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, unloadResponse.StatusCode);
        await modelService.DidNotReceiveWithAnyArgs().ListLocalModelsAsync(Arg.Any<CancellationToken>());
        await modelService.DidNotReceiveWithAnyArgs().ListRunningModelsAsync(Arg.Any<CancellationToken>());
        await modelService.DidNotReceiveWithAnyArgs().UnloadModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static TestServerWebAppFactory CreateFactory(IOllamaModelService modelService)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IOllamaModelService>();
                services.AddSingleton(modelService);
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton<INodeSettingsStore>(new StubNodeSettingsStore());
            }
        };
    }

    private sealed class StubNodeSettingsStore : INodeSettingsStore
    {
        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new StoredNodeSettings());
        }

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            return new StoredNodeSettings();
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
