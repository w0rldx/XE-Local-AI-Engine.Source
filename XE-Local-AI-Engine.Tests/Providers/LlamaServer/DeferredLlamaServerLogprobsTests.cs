namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The send and read path of token probabilities through the deferred client's real body patches and the real MEAI
///     adapter, against a loopback server replaying chunks shaped like a b12014 stream.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DeferredLlamaServerLogprobsTests
{
    [Test]
    public async Task Streaming_SendsTheCountBesideTheThinkingSwitch_AndEveryEntryStaysReadable()
    {
        using var server = DeferredLlamaServerResponseSchemaEntryPointTests.CapturingServer.Start(ThinkingStream, "text/event-stream");
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor { EnsureEndpoint = server.BaseAddress }, "qwen3:8b", TimeSpan.FromSeconds(30));
        var options = LlamaServerLogprobs.Request(new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { [DeferredLlamaServerChatClient.DisableThinkingMarkerKey] = true }
            },
            topLogprobs: 2);

        List<string> tokens = [];
        List<string> sources = [];
        var text = string.Empty;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None))
        {
            text += update.Text;
            var source = update.Contents.OfType<TextReasoningContent>().Any() ? "reasoning" : "answer";
            foreach (var entry in LlamaServerLogprobs.Read(update.RawRepresentation))
            {
                tokens.Add(entry.Token);
                sources.Add(source);
            }
        }

        using var body = JsonDocument.Parse(AssertEx.NotNull(server.CapturedBody));
        AssertEx.True(body.RootElement.GetProperty("logprobs").GetBoolean());
        AssertEx.Equal(2, body.RootElement.GetProperty("top_logprobs").GetInt32());
        AssertEx.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(), "the provider's own patch composes with this one");
        AssertEx.Equal("reasoning,answer", string.Join(",", sources));
        AssertEx.Equal("1,ok", string.Join(",", tokens), "an omitted entry stays omitted: the held-back newline has none");
        AssertEx.Equal("ok\n", text, "text with no entry still streams, and the final chunk's gap fails nothing");
    }

    private const string ThinkingStream =
        "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"\\n1\"},"
        + "\"logprobs\":{\"content\":[{\"id\":16,\"token\":\"1\",\"bytes\":[49],\"logprob\":-0.25,\"top_logprobs\":[{\"id\":16,\"token\":\"1\",\"bytes\":[49],\"logprob\":-0.25}]}]}}]}\n\n"
        + "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{\"content\":\"ok\"},"
        + "\"logprobs\":{\"content\":[{\"id\":2,\"token\":\"ok\",\"bytes\":[111,107],\"logprob\":-0.5,\"top_logprobs\":[{\"id\":2,\"token\":\"ok\",\"bytes\":[111,107],\"logprob\":-0.5}]}]}}]}\n\n"
        + "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{\"content\":\"\\n\"}}]}\n\n"
        + "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"delta\":{}}]}\n\n"
        + "data: [DONE]\n\n";
}
