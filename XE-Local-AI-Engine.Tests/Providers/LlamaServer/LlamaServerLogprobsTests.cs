namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the parsed shape of llama-server's <c>logprobs</c> chunks, from captures on the pinned b12014 with the model
///     path removed, and the request body the real MEAI adapter sends. A pin bump that moves the shape fails here.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LlamaServerLogprobsTests
{
    private const string AsciiChunkJson = """
                                          {"choices":[{"finish_reason":null,"index":0,"delta":{"content":"Red"},
                                           "logprobs":{"content":[{"id":1114,"token":"Red","bytes":[82,101,100],"logprob":-1.743,
                                            "top_logprobs":[{"id":2,"token":"red","bytes":[114,101,100],"logprob":-1.690},
                                                            {"id":1114,"token":"Red","bytes":[82,101,100],"logprob":-1.743}]}]}}],
                                           "created":1,"id":"c","model":"m","object":"chat.completion.chunk"}
                                          """;

    private const string MultiByteChunkJson = """
                                              {"choices":[{"finish_reason":null,"index":0,"delta":{"content":"🌏"},
                                               "logprobs":{"content":[{"id":237,"token":"🌏","bytes":[240,159,140,143],"logprob":-0.013833336532115936,
                                                "top_logprobs":[{"id":237,"token":"�","bytes":[143],"logprob":-0.013833336532115936},
                                                                {"id":243,"token":"�","bytes":[149],"logprob":-5.499370098114014}]}]}}],
                                               "created":1,"id":"c","model":"m","object":"chat.completion.chunk"}
                                              """;

    [Test]
    public void Read_AnAsciiEntry_GivesItsTextValueAndAlternatives()
    {
        var entry = LlamaServerLogprobs.Read(Chunk(AsciiChunkJson)).Single();

        AssertEx.Equal("Red", entry.Token);
        AssertEx.Equal(-1.743f, entry.LogProbability);
        AssertEx.Equal(2, entry.TopLogProbabilities.Count);
        AssertEx.Equal("red", entry.TopLogProbabilities[0].Token);
        AssertEx.Equal(-1.690f, entry.TopLogProbabilities[0].LogProbability);
    }

    [Test]
    public void Read_AMergedMultiByteEntry_KeepsTheWholeTextWithTheLastTokensAlternatives()
    {
        var entry = LlamaServerLogprobs.Read(Chunk(MultiByteChunkJson)).Single();

        AssertEx.Equal("🌏", entry.Token);
        AssertEx.Equal("�", entry.TopLogProbabilities[0].Token);
    }

    [Test]
    [Arguments("""{"choices":[{"index":0,"delta":{"content":"hi"}}],"created":1,"id":"c","model":"m","object":"chat.completion.chunk"}""")]
    [Arguments("""{"choices":[{"index":0,"delta":{"content":"hi"},"logprobs":null}],"created":1,"id":"c","model":"m","object":"chat.completion.chunk"}""")]
    [Arguments("""{"choices":[{"index":0,"delta":{"content":"hi"},"logprobs":{"content":null}}],"created":1,"id":"c","model":"m","object":"chat.completion.chunk"}""")]
    [Arguments("""{"choices":[],"created":1,"id":"c","model":"m","object":"chat.completion.chunk","usage":{"completion_tokens":1,"prompt_tokens":1,"total_tokens":2}}""")]
    public void Read_AChunkWithoutEntries_IsEmpty(string chunkJson)
    {
        AssertEx.Empty(LlamaServerLogprobs.Read(Chunk(chunkJson)));
    }

    [Test]
    public void Read_SomethingThatIsNotAChatChunk_IsEmpty()
    {
        AssertEx.Empty(LlamaServerLogprobs.Read(rawRepresentation: null));
        AssertEx.Empty(LlamaServerLogprobs.Read("not an OpenAI chunk"));
    }

    [Test]
    public async Task Request_ComposesWithAPriorPatch_AndLeavesTheCallersOptionsAlone()
    {
        var options = new ChatOptions
        {
            RawRepresentationFactory = static _ => new ChatCompletionOptions
            {
                EndUserId = "prior"
            }
        };

        var requested = LlamaServerLogprobs.Request(options, topLogprobs: 7);

        using var body = JsonDocument.Parse(await LlamaGrammarToolOffer.CaptureWireBodyAsync(requested, CancellationToken.None));
        AssertEx.True(body.RootElement.GetProperty("logprobs").GetBoolean());
        AssertEx.Equal(7, body.RootElement.GetProperty("top_logprobs").GetInt32());
        AssertEx.Equal("prior", body.RootElement.GetProperty("user").GetString());
        using var untouched = JsonDocument.Parse(await LlamaGrammarToolOffer.CaptureWireBodyAsync(options, CancellationToken.None));
        AssertEx.False(untouched.RootElement.TryGetProperty("logprobs", out _));
    }

    private static StreamingChatCompletionUpdate Chunk(string json) =>
        ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))
        ?? throw new InvalidOperationException("The chunk fixture did not deserialize.");
}
