namespace XE_Local_AI_Engine.Tests.Capacity;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class SpawnSubAgentToolHandlerTests
{
    [Test]
    public async Task ExecuteAsync_WhenAnyStringExceedsSchemaBound_RejectsBeforeResolvingOrCallingSpawnService()
    {
        var spawnService = Substitute.For<ISubAgentSpawnService>();
        var services = new ServiceCollection();
        services.AddScoped(_ => spawnService);
        await using var provider = services.BuildServiceProvider();
        var handler = new SpawnSubAgentToolHandler(provider.GetRequiredService<IServiceScopeFactory>());
        SubAgentSpawnRequest[] invalidRequests =
        [
            new()
            {
                SubAgentKey = new string('k', 257),
                Task = "task"
            },
            new()
            {
                ModelId = new string('m', 257),
                Task = "task"
            },
            new()
            {
                ModelId = "model",
                Task = new string('t', 8001)
            },
            new()
            {
                ModelId = "model",
                Task = "task",
                Instructions = new string('i', 8001)
            }
        ];

        foreach (var request in invalidRequests)
        {
            var result = await handler.ExecuteAsync(JsonSerializer.Serialize(request));
            AssertEx.True(result.Contains("exceeded", StringComparison.OrdinalIgnoreCase), "Oversized arguments must return a bounded validation failure.");
        }

        await spawnService.DidNotReceive().SpawnAsync(Arg.Any<SubAgentSpawnRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WhenUnknownMemberIsPresent_RejectsBeforeCallingSpawnService()
    {
        var (handler, spawnService, provider) = CreateHandler();
        await using (provider)
        {
            var result = await handler.ExecuteAsync("{\"modelId\":\"model\",\"task\":\"task\",\"unexpected\":true}");

            AssertEx.True(result.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase));
            await spawnService.DidNotReceive().SpawnAsync(Arg.Any<SubAgentSpawnRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenUnknownMemberMakesRawPayloadOversized_RejectsBeforeDeserializationOrSpawn()
    {
        var (handler, spawnService, provider) = CreateHandler();
        await using (provider)
        {
            var payload = "{\"modelId\":\"model\",\"task\":\"task\",\"padding\":\"" + new string('x', 20000) + "\"}";
            var result = await handler.ExecuteAsync(payload);

            AssertEx.True(result.Contains("payload", StringComparison.OrdinalIgnoreCase));
            await spawnService.DidNotReceive().SpawnAsync(Arg.Any<SubAgentSpawnRequest>(), Arg.Any<CancellationToken>());
        }
    }

    private static (SpawnSubAgentToolHandler Handler, ISubAgentSpawnService SpawnService, ServiceProvider Provider) CreateHandler()
    {
        var spawnService = Substitute.For<ISubAgentSpawnService>();
        var services = new ServiceCollection();
        services.AddScoped(_ => spawnService);
        var provider = services.BuildServiceProvider();
        return (new SpawnSubAgentToolHandler(provider.GetRequiredService<IServiceScopeFactory>()), spawnService, provider);
    }
}
