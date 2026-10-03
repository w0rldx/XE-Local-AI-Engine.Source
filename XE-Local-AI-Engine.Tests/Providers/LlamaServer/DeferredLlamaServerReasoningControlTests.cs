namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     "Answer now" needs the streaming request to ARM llama-server's reasoning control and the stream to say which
///     completion and server to address.
/// </summary>
/// <remarks>
///     Driven through the client's own streaming entry point against a loopback server, so the MEAI OpenAI adapter's
///     <c>ResponseId</c> mapping of the chunk's <c>chatcmpl-…</c> id is exercised, not assumed.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class DeferredLlamaServerReasoningControlTests
{
    private const string CompletionId = "chatcmpl-answer-now-7";

    [Test]
    public async Task Streaming_WithABudget_ArmsTheControlAndAdvertisesTheCompletionOnce()
    {
        using var server = DeferredLlamaServerResponseSchemaEntryPointTests.CapturingServer.Start(ReasoningStream, "text/event-stream");
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor
        {
            EnsureEndpoint = server.BaseAddress
        }, "qwen3:8b", TimeSpan.FromSeconds(30));

        var advertised = new List<(Uri BaseAddress, string CompletionId)>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], BudgetOptions(8192), CancellationToken.None))
        {
            if (LlamaServerReasoningControl.TryRead(update, out var baseAddress, out var completionId))
            {
                advertised.Add((baseAddress, completionId));
            }
        }

        using var body = JsonDocument.Parse(AssertEx.NotNull(server.CapturedBody));
        AssertEx.True(body.RootElement.GetProperty("reasoning_control").GetBoolean(), "the request must arm the realtime reasoning control.");
        AssertEx.Equal(8192, body.RootElement.GetProperty("reasoning_budget_tokens").GetInt32());

        AssertEx.Equal(1, advertised.Count, "the completion is advertised on its first update only.");
        var only = advertised[0];
        AssertEx.Equal(CompletionId, only.CompletionId, "the id must be llama-server's own chatcmpl id, the one the control route matches.");
        AssertEx.Equal(server.BaseAddress, only.BaseAddress);
    }

    [Test]
    public async Task Streaming_WithoutABudget_NeitherArmsNorAdvertises()
    {
        using var server = DeferredLlamaServerResponseSchemaEntryPointTests.CapturingServer.Start(ReasoningStream, "text/event-stream");
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor
        {
            EnsureEndpoint = server.BaseAddress
        }, "qwen3:8b", TimeSpan.FromSeconds(30));

        var advertisedCount = 0;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions(), CancellationToken.None))
        {
            if (LlamaServerReasoningControl.TryRead(update, out _, out _))
            {
                advertisedCount++;
            }
        }

        AssertEx.Equal(0, advertisedCount, "thinking off or an unenforceable template sends no budget, so there is nothing to end early.");
        AssertEx.False(AssertEx.NotNull(server.CapturedBody).Contains("reasoning_control", StringComparison.Ordinal));
    }

    [Test]
    public void ApplyReasoningControl_WithoutTheBudgetMarker_ReturnsTheSameOptions()
    {
        var options = new ChatOptions();

        AssertEx.True(ReferenceEquals(options, DeferredLlamaServerChatClient.ApplyReasoningControl(options)));
        AssertEx.Null(DeferredLlamaServerChatClient.ApplyReasoningControl(null));
    }

    private static ChatOptions BudgetOptions(int budgetTokens) =>
        new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.ReasoningBudgetMarkerKey] = budgetTokens
            }
        };

    private const string ReasoningStream =
        "data: {\"id\":\"" + CompletionId + "\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"thinking\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"" + CompletionId + "\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"id\":\"" + CompletionId + "\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";
}
