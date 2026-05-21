namespace XE_Local_AI_Engine.Tests.HostAgent;

using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;
using XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;
using XE_Local_AI_Engine.HostAgent.Linux.Models;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BootstrapModelReadinessServiceTests
{
    [Test]
    public async Task EnsureReadyAsync_WhenBootstrapModelAlreadyExists_DoesNotPullAgain()
    {
        await using var fakeOllama = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = ["qwen3:0.6b"]
        });
        using var service = CreateService(fakeOllama.BaseAddress);

        var snapshot = await service.ReadinessService.EnsureReadyAsync(CancellationToken.None);

        AssertEx.True(snapshot.IsReady);
        AssertEx.False(fakeOllama.RecordedRequests.Any(request => request.Path == "/api/pull"));
    }

    [Test]
    public async Task EnsureReadyAsync_WhenBootstrapModelIsMissing_PullsAndMarksReady()
    {
        await using var fakeOllama = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = ["embeddings"]
        });
        using var service = CreateService(fakeOllama.BaseAddress);

        var snapshot = await service.ReadinessService.EnsureReadyAsync(CancellationToken.None);

        AssertEx.True(snapshot.IsReady);
        AssertEx.Contains(fakeOllama.RecordedRequests, request => request.Path == "/api/pull" && request.ModelName == "qwen3:0.6b");
    }

    private static TestReadinessService CreateService(Uri ollamaEndpoint)
    {
        var client = new OllamaApiClient(ollamaEndpoint);
        var readinessService = new BootstrapModelReadinessService(new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            },
            client,
            TimeProvider.System,
            NullLogger<BootstrapModelReadinessService>.Instance);
        return new TestReadinessService(readinessService, client);
    }

    private static HostAgentManifest CreateManifest()
    {
        return new HostAgentManifest
        {
            SchemaVersion = 1,
            RuntimeMode = "managed",
            Models = new ModelManifest
            {
                BootstrapModel = "qwen3:0.6b",
                DefaultChatModel = "qwen3:8b"
            },
            Containers = [],
            RuntimeLimits = new RuntimeLimitsManifest
            {
                MaxRuntimeDiskGb = 128,
                StopDrainTimeoutSeconds = 30
            }
        };
    }

    private sealed class TestReadinessService : IDisposable
    {
        private readonly OllamaApiClient _client;

        public TestReadinessService(BootstrapModelReadinessService readinessService, OllamaApiClient client)
        {
            ReadinessService = readinessService;
            _client = client;
        }

        public BootstrapModelReadinessService ReadinessService { get; }

        public void Dispose()
        {
            ReadinessService.Dispose();
            _client.Dispose();
        }
    }
}
