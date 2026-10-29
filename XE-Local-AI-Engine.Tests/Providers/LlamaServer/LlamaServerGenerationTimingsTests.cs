namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.ClientModel.Primitives;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins how llama-server's <c>timings</c> object is lifted off a streamed chunk. Each of three invariants fails
///     silently if it stops holding.
/// </summary>
/// <remarks>
///     (1) The field is reachable: it is not in OpenAI's schema, so it lives only on the SDK's experimental <c>Patch</c>
///     surface, and a package bump that moves it nulls every benchmark's pp/tg split. (2) The Agent-Framework hop is
///     followed: <c>AgentResponseUpdate</c> wraps the MEAI <c>ChatResponseUpdate</c>, which wraps the OpenAI chunk.
///     (3) A chunk without timings (every cloud provider, every non-final llama-server chunk) yields null rather than
///     throwing, because the reader sits on the hot streaming path of every turn.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class LlamaServerGenerationTimingsTests
{
    private const string TimingsChunkJson = """
                                            {"id":"chunk","object":"chat.completion.chunk","created":1,"model":"m",
                                             "choices":[{"index":0,"finish_reason":"stop","delta":{}}],
                                             "timings":{"cache_n":7,"prompt_n":123,"prompt_ms":456.5,"prompt_per_second":269.4,
                                                        "predicted_n":89,"predicted_ms":1011.5,"predicted_per_second":88.0}}
                                            """;

    private const string PlainChunkJson = """
                                          {"id":"chunk","object":"chat.completion.chunk","created":1,"model":"m",
                                           "choices":[{"index":0,"delta":{"content":"hi"}}]}
                                          """;

    [Test]
    public void TryRead_FromAnAgentUpdateWrappingALlamaServerChunk_ReadsEveryTiming()
    {
        var timings = LlamaServerGenerationTimings.ReadOrNull(AgentUpdate(TimingsChunkJson).RawRepresentation);

        var measured = AssertEx.NotNull(timings, "The final chunk of a llama-server stream always carries timings.");
        AssertEx.Equal<int?>(123, measured.PromptTokens);
        AssertEx.Equal<double?>(456.5, measured.PromptMs);
        AssertEx.Equal<int?>(89, measured.GenerationTokens);
        AssertEx.Equal<double?>(1011.5, measured.GenerationMs);
        AssertEx.Equal<int?>(7, measured.CachedPromptTokens);
    }

    [Test]
    public void TryRead_FromAChunkWithoutTimings_IsNull()
    {
        // The guard that keeps this off every cloud turn's hot path: absent means unmeasured, not an exception and not a
        // zero-valued measurement.
        AssertEx.Null(LlamaServerGenerationTimings.ReadOrNull(AgentUpdate(PlainChunkJson).RawRepresentation));
        AssertEx.Null(LlamaServerGenerationTimings.ReadOrNull(rawRepresentation: null));
        AssertEx.Null(LlamaServerGenerationTimings.ReadOrNull("not an OpenAI chunk"));
    }

    [Test]
    public void TryRead_WhenTheServerReportsAnUntimedResult_TreatsNegativeCountsAsAbsent()
    {
        // llama-server emits prompt_n = -1 for a result it did not time. Persisting that as a token count would put a
        // negative number in the pp column and a negative tok/s on the screen.
        const string untimed = """
                               {"id":"chunk","object":"chat.completion.chunk","created":1,"model":"m",
                                "choices":[{"index":0,"finish_reason":"stop","delta":{}}],
                                "timings":{"cache_n":0,"prompt_n":-1,"prompt_ms":-1.0,"predicted_n":-1,"predicted_ms":-1.0}}
                               """;

        AssertEx.Null(LlamaServerGenerationTimings.ReadOrNull(AgentUpdate(untimed).RawRepresentation));
    }

    // The exact production chain: OpenAI chunk -> MEAI ChatResponseUpdate.RawRepresentation -> AgentResponseUpdate.
    private static AgentResponseUpdate AgentUpdate(string chunkJson)
    {
        var chunk = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(chunkJson))
                    ?? throw new InvalidOperationException("The chunk fixture did not deserialize.");
        return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, "hi")
        {
            RawRepresentation = chunk
        });
    }
}
