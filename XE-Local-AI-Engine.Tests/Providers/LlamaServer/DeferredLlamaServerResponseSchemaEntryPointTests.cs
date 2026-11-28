namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The same contract <see cref="DeferredLlamaServerStructuredOutputTests" /> pins, asserted through
///     <see cref="DeferredLlamaServerChatClient" />'s OWN entry points rather than a hand-mirrored transform chain.
///     Those tests compose the <c>Apply*</c> family themselves, so deleting
///     <see cref="DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough" /> from
///     <c>GetResponseAsync</c> or <c>GetStreamingResponseAsync</c> leaves every one of them green — the wiring is the
///     half they cannot see. These two cover it, once per entry point, because the streaming path composes the chain in
///     its own statement and has already been the half that drifted.
/// </summary>
/// <remarks>
///     The client builds its inner adapter itself (<c>LlamaServerOpenAIAdapterFactory.CreateChatClient</c>) from an
///     endpoint the supervisor hands it, with no injectable message handler, so the only seam that reaches it is a real
///     one: a loopback <see cref="HttpListener" /> behind <see cref="FakeProcessSupervisor" />. Same shape as
///     <see cref="DeferredLlamaServerEmbeddingGeneratorFailureTests" />, which drives the sibling client that way for
///     the same reason.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class DeferredLlamaServerResponseSchemaEntryPointTests
{
    private const string AuthoredSchema =
        """
        {"type":"object","properties":{
          "answer":{"type":"string","minLength":2,"maxLength":3,"pattern":"^[a-z]{2,3}$"},
          "notes":{"type":"string"}
        },"required":["answer"]}
        """;

    [Test]
    public async Task GetResponseAsync_SendsTheAuthoredSchemaAndTheThinkingSwitch()
    {
        using var server = CapturingServer.Start(CompletionBody, "application/json");
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor
            {
                EnsureEndpoint = server.BaseAddress
            },
            "qwen3:8b",
            TimeSpan.FromSeconds(30));

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], SchemaOptions(), CancellationToken.None);

        AssertWireBody(server.CapturedBody);
    }

    [Test]
    public async Task GetStreamingResponseAsync_SendsTheAuthoredSchemaAndTheThinkingSwitch()
    {
        using var server = CapturingServer.Start(StreamingBody, "text/event-stream");
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor
            {
                EnsureEndpoint = server.BaseAddress
            },
            "qwen3:8b",
            TimeSpan.FromSeconds(30));

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], SchemaOptions(), CancellationToken.None))
        {
            // Drained rather than inspected: the request body is what this pins, and the stream must be pulled at least
            // once for the client to open the connection at all.
        }

        AssertWireBody(server.CapturedBody);
    }

    private static ChatOptions SchemaOptions()
    {
        using var schema = JsonDocument.Parse(AuthoredSchema);
        return new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), "teacher_sample"),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.DisableThinkingMarkerKey] = true
            }
        };
    }

    private static void AssertWireBody(string? captured)
    {
        using var body = JsonDocument.Parse(AssertEx.NotNull(captured, "the client must have sent a request body."));
        var schema = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        var answer = schema.GetProperty("properties").GetProperty("answer");

        AssertEx.Equal(expected: 3, answer.GetProperty("maxLength").GetInt32(),
            "the authored bound must be on the wire: llama-server compiles this into the grammar, and the MEAI strict transform would have moved it into description prose.");
        AssertEx.Equal("^[a-z]{2,3}$", answer.GetProperty("pattern").GetString());
        AssertEx.Equal("answer",
            string.Join(',', schema.GetProperty("required").EnumerateArray().Select(static entry => entry.GetString())),
            "'notes' was left optional by the author and the transform would have made it required.");

        // Alongside, not instead of: both patches share the single RawRepresentationFactory slot, and this entry point
        // is where they are actually composed.
        AssertEx.Equal(expected: false, body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

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
}
