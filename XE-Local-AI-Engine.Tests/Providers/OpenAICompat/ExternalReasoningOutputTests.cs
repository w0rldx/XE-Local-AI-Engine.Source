namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Reasoning-output recovery, driven through the whole production stack over a replaying transport so the pinned
///     MEAI adapter's own behavior is part of what is being measured — not mocked away.
/// </summary>
/// <remarks>
///     The load-bearing assertion in the <c>reasoning_content</c> cases is that the reasoning appears exactly ONCE. The
///     pinned Microsoft.Extensions.AI.OpenAI 10.9.0 adapter already maps that field itself, so a rewriter that "helpfully"
///     converted it too would show the user their model's thinking twice — a regression no assertion on presence alone
///     would catch.
/// </remarks>
public sealed class ExternalReasoningOutputTests
{
    [Test]
    public async Task NonStreaming_NativeReasoningContent_IsSurfacedExactlyOnce()
    {
        var response = await CompleteAsync("answer", "\"reasoning_content\":\"THOUGHT\"");

        AssertEx.Equal("THOUGHT", Reasoning(response));
        AssertEx.Equal("answer", Text(response));
        AssertEx.Equal(1, response.Messages.SelectMany(message => message.Contents).Count(content => content is TextReasoningContent));
    }

    [Test]
    public async Task NonStreaming_VllmReasoningField_IsRecoveredFromTheRawPayload()
    {
        // The typed OpenAI schema has no `reasoning` property, so the MEAI adapter drops it; without this recovery the
        // model's thinking would simply vanish on a newer vLLM build.
        var response = await CompleteAsync("answer", "\"reasoning\":\"VLLM-THOUGHT\"");

        AssertEx.Equal("VLLM-THOUGHT", Reasoning(response));
        AssertEx.Equal("answer", Text(response));
    }

    [Test]
    public async Task NonStreaming_InlineThinkTags_AreSplitOutOfTheAnswer()
    {
        var response = await CompleteAsync("<think>inline thought</think>the answer");

        AssertEx.Equal("inline thought", Reasoning(response));
        AssertEx.Equal("the answer", Text(response));
    }

    [Test]
    public async Task NonStreaming_PlainContent_IsLeftExactlyAsItArrived()
    {
        var response = await CompleteAsync("just an answer with a <think> word inside it");

        AssertEx.Equal(string.Empty, Reasoning(response));
        AssertEx.Equal("just an answer with a <think> word inside it", Text(response));
    }

    [Test]
    public async Task Streaming_NativeReasoningContent_IsSurfacedExactlyOnce()
    {
        var updates = await StreamAsync("{\"role\":\"assistant\",\"reasoning_content\":\"THO\"}",
            "{\"reasoning_content\":\"UGHT\"}",
            "{\"content\":\"answer\"}");

        AssertEx.Equal("THOUGHT", Reasoning(updates));
        AssertEx.Equal("answer", Text(updates));
    }

    [Test]
    public async Task Streaming_VllmReasoningField_IsRecoveredPerDelta()
    {
        var updates = await StreamAsync("{\"role\":\"assistant\",\"reasoning\":\"VLLM-\"}",
            "{\"reasoning\":\"THOUGHT\"}",
            "{\"content\":\"answer\"}");

        AssertEx.Equal("VLLM-THOUGHT", Reasoning(updates));
        AssertEx.Equal("answer", Text(updates));
    }

    [Test]
    public async Task Streaming_InlineThinkTagsSplitAcrossDeltas_AreStillRecognised()
    {
        // A tag arriving in pieces is the normal case over SSE, and it is exactly where a naive per-chunk StartsWith
        // check fails: neither "<thi" nor "nk>reasoning " is a think tag on its own.
        var updates = await StreamAsync("{\"role\":\"assistant\",\"content\":\"<thi\"}",
            "{\"content\":\"nk>reasoning \"}",
            "{\"content\":\"more</thi\"}",
            "{\"content\":\"nk>answer\"}");

        AssertEx.Equal("reasoning more", Reasoning(updates));
        AssertEx.Equal("answer", Text(updates));
    }

    [Test]
    public async Task Streaming_UnterminatedThinkBlock_StillYieldsTheBufferedText()
    {
        // A truncated stream must not swallow what the model did produce.
        var updates = await StreamAsync("{\"role\":\"assistant\",\"content\":\"<think>cut off\"}");

        AssertEx.Equal("cut off", Reasoning(updates));
        AssertEx.Equal(string.Empty, Text(updates));
    }

    [Test]
    public async Task Streaming_PlainContent_IsLeftExactlyAsItArrived()
    {
        var updates = await StreamAsync("{\"role\":\"assistant\",\"content\":\"hello \"}",
            "{\"content\":\"world\"}");

        AssertEx.Equal(string.Empty, Reasoning(updates));
        AssertEx.Equal("hello world", Text(updates));
    }

    private static async Task<ChatResponse> CompleteAsync(string content, string? extraMessageJson = null)
    {
        var recorder = new OpenAiWireRecorder
        {
            Responder = _ => OpenAiWireRecorder.Completion(content, extraMessageJson)
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(supportsReasoning: true));
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);
        return await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<ChatResponseUpdate>> StreamAsync(params string[] deltaJson)
    {
        var recorder = new OpenAiWireRecorder
        {
            Responder = _ => OpenAiWireRecorder.Stream(deltaJson)
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(supportsReasoning: true));
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static string Reasoning(ChatResponse response)
    {
        return Concat(response.Messages.SelectMany(message => message.Contents));
    }

    private static string Text(ChatResponse response)
    {
        return ConcatText(response.Messages.SelectMany(message => message.Contents));
    }

    private static string Reasoning(IEnumerable<ChatResponseUpdate> updates)
    {
        return Concat(updates.SelectMany(update => update.Contents));
    }

    private static string Text(IEnumerable<ChatResponseUpdate> updates)
    {
        return ConcatText(updates.SelectMany(update => update.Contents));
    }

    private static string Concat(IEnumerable<AIContent> contents)
    {
        return string.Concat(contents.OfType<TextReasoningContent>().Select(content => content.Text));
    }

    private static string ConcatText(IEnumerable<AIContent> contents)
    {
        return string.Concat(contents.OfType<TextContent>().Select(content => content.Text));
    }
}
