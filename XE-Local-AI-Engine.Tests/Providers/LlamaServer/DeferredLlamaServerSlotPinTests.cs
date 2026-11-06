namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using CapturingServer = DeferredLlamaServerResponseSchemaEntryPointTests.CapturingServer;

/// <summary>
///     Every request a <see cref="DeferredLlamaServerChatClient" /> sends names the chat process's slot: background work
///     goes to <see cref="LlamaServerChatSlots.Background" />, everything else to <see cref="LlamaServerChatSlots.Foreground" />.
/// </summary>
/// <remarks>
///     Asserted through both entry points over a real loopback server, because each composes the body-patch chain in its
///     own statement.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class DeferredLlamaServerSlotPinTests
{
    private const string CompletionBody =
        "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],"
        + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    private const string StreamingBody =
        "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Test]
    [Arguments(ModelResidencyIntent.Interactive, LlamaServerChatSlots.Foreground)]
    [Arguments(ModelResidencyIntent.Transient, LlamaServerChatSlots.Foreground)]
    [Arguments(ModelResidencyIntent.Background, LlamaServerChatSlots.Background)]
    public async Task GetResponseAsync_NamesTheSlotOfTheClientsIntent(ModelResidencyIntent intent, int expectedSlot)
    {
        using var server = CapturingServer.Start(CompletionBody, "application/json");
        using var client = CreateClient(server, intent);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], ThinkingOffOptions(), CancellationToken.None);

        AssertWireBody(server.CapturedBody, expectedSlot);
    }

    [Test]
    [Arguments(ModelResidencyIntent.Interactive, LlamaServerChatSlots.Foreground)]
    [Arguments(ModelResidencyIntent.Transient, LlamaServerChatSlots.Foreground)]
    [Arguments(ModelResidencyIntent.Background, LlamaServerChatSlots.Background)]
    public async Task GetStreamingResponseAsync_NamesTheSlotOfTheClientsIntent(ModelResidencyIntent intent, int expectedSlot)
    {
        using var server = CapturingServer.Start(StreamingBody, "text/event-stream");
        using var client = CreateClient(server, intent);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], ThinkingOffOptions(), CancellationToken.None))
        {
            // Drained so the request is sent; the body is what this pins.
        }

        AssertWireBody(server.CapturedBody, expectedSlot);
    }

    [Test]
    public async Task GetResponseAsync_WithNoOptions_StillNamesTheForegroundSlot()
    {
        using var server = CapturingServer.Start(CompletionBody, "application/json");
        using var client = CreateClient(server, ModelResidencyIntent.Interactive);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        using var body = JsonDocument.Parse(AssertEx.NotNull(server.CapturedBody, "the client must have sent a request body."));
        AssertEx.Equal(LlamaServerChatSlots.Foreground, body.RootElement.GetProperty("id_slot").GetInt32());
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Background_OnASingleSlotProcess_NamesSlotZero(bool streaming)
    {
        // The single-slot fallback launches --parallel 1: slot 1 does not exist there, so the pin follows the launched shape.
        using var server = CapturingServer.Start(streaming ? StreamingBody : CompletionBody, streaming ? "text/event-stream" : "application/json");
        using var client = CreateClient(server, ModelResidencyIntent.Background, parallelSlots: 1);

        if (streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], ThinkingOffOptions(), CancellationToken.None))
            {
                // Drained so the request is sent.
            }
        }
        else
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], ThinkingOffOptions(), CancellationToken.None);
        }

        AssertWireBody(server.CapturedBody, LlamaServerChatSlots.Foreground);
    }

    [Test]
    [Arguments(1, LlamaServerChatSlots.Foreground)]
    [Arguments(LlamaServerChatSlots.Count, LlamaServerChatSlots.Background)]
    public async Task AdmittedSlotCount_ReachesTheArgvTheEndpointAndTheBackgroundRequest(int admittedSlots, int expectedBackgroundSlot)
    {
        // Admission drops the background slot before the window: that launch runs --parallel 1, so background work on it must name slot 0.
        var launcher = new FakeProcessLauncher();
        var registry = new ProcessLaunchAdmissionRegistry();
        await using var supervisor = SupervisorFactory.Create(launcher, variantSelector: new FakeVariantSelector(GpuVariant.Cpu), launchAdmissions: registry);
        AssertEx.True(registry.TryAcquire(ChatAdmission(admittedSlots), out var consumer));
        LlamaServerEndpoint endpoint;
        using (consumer)
        {
            endpoint = await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);
        }

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal(admittedSlots.ToString(CultureInfo.InvariantCulture), spec!.Arguments.SkipWhile(static a => a != "--parallel").ElementAt(1));
        AssertEx.Equal(admittedSlots, endpoint.ParallelSlots);

        using var server = CapturingServer.Start(CompletionBody, "application/json");
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor
            {
                EnsureEndpoint = server.BaseAddress,
                EnsureParallelSlots = endpoint.ParallelSlots
            },
            "chat-model",
            TimeSpan.FromSeconds(30),
            residencyIntent: ModelResidencyIntent.Background);
        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        using var body = JsonDocument.Parse(AssertEx.NotNull(server.CapturedBody, "the client must have sent a request body."));
        AssertEx.Equal(expectedBackgroundSlot, body.RootElement.GetProperty("id_slot").GetInt32());
    }

    private static ProcessLaunchAdmission ChatAdmission(int chatSlots) =>
        new()
        {
            ModelName = "chat-model",
            Role = ModelRole.Chat,
            Variant = GpuVariant.Cpu,
            ResolvedArguments = ResolvedLaunchArguments.Explore(),
            Allocation = new ProcessContextAllocation
            {
                ProcessContextTokens = 2048,
                ModelTrainContextTokens = 8192,
                Source = ProcessContextAllocationSource.HardwareTier,
                Placement = ProcessPlacementMode.Cpu,
                Footprint = ResourceFootprint.Zero,
                ContentIdentity = "chat-model:0",
                CacheKey = "cache:chat-model",
                ChatSlots = chatSlots
            }
        };

    private static DeferredLlamaServerChatClient CreateClient(CapturingServer server, ModelResidencyIntent intent, int parallelSlots = LlamaServerChatSlots.Count) =>
        new(new FakeProcessSupervisor
            {
                EnsureEndpoint = server.BaseAddress,
                EnsureParallelSlots = parallelSlots
            },
            "qwen3:8b",
            TimeSpan.FromSeconds(30),
            residencyIntent: intent);

    // The thinking switch is an earlier patch on the same single factory slot; it must survive the slot pin composing after it.
    private static ChatOptions ThinkingOffOptions() =>
        new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.DisableThinkingMarkerKey] = true
            }
        };

    private static void AssertWireBody(string? captured, int expectedSlot)
    {
        using var body = JsonDocument.Parse(AssertEx.NotNull(captured, "the client must have sent a request body."));
        AssertEx.Equal(expectedSlot, body.RootElement.GetProperty("id_slot").GetInt32());
        AssertEx.Equal(expected: false, body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }
}
