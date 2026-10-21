namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompatible;

using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

#pragma warning disable OPENAI001 // The builder takes the experimental Responses effort type.
/// <summary>
///     The shared stateless Responses builder, asserted on the body the real MEAI Responses adapter serializes over a
///     recording transport, because what matters is which fields reach the wire, not what the options object holds.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class OpenAICompatibleResponsesRequestTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:1/v1/");

    [Test]
    public async Task Stateless_WithReasoning_SendsStoreFalseTheSummaryTheEffortAndTheEncryptedInclude()
    {
        var root = await SendAsync(OpenAICompatibleResponsesRequest.Stateless(options: null,
            includeReasoning: true,
            ResponseReasoningEffortLevel.High,
            parallelToolCalls: false));

        AssertEx.Equal(JsonValueKind.False, root.GetProperty("store").ValueKind);
        AssertEx.False(root.TryGetProperty("previous_response_id", out _), "a stateless request must not reference a stored response.");
        AssertEx.False(root.TryGetProperty("conversation", out _), "a stateless request must not reference a conversation.");
        var reasoning = root.GetProperty("reasoning");
        AssertEx.Equal("auto", reasoning.GetProperty("summary").GetString());
        AssertEx.Equal("high", reasoning.GetProperty("effort").GetString());
        AssertEx.Contains(root.GetProperty("include").EnumerateArray().Select(static item => item.GetString()), "reasoning.encrypted_content");
        AssertEx.Equal(JsonValueKind.False, root.GetProperty("parallel_tool_calls").ValueKind);
    }

    [Test]
    public async Task Stateless_WithoutReasoning_OmitsTheReasoningBlockAndTheIncludeButKeepsStoreFalse()
    {
        // A model that does not reason may answer 400 to either field, so neither rides, and the effort is ignored.
        var root = await SendAsync(OpenAICompatibleResponsesRequest.Stateless(options: null,
            includeReasoning: false,
            ResponseReasoningEffortLevel.High,
            parallelToolCalls: null));

        AssertEx.Equal(JsonValueKind.False, root.GetProperty("store").ValueKind);
        AssertEx.False(root.TryGetProperty("reasoning", out _), "no reasoning block may be sent for a non-reasoning model.");
        AssertEx.False(root.TryGetProperty("include", out _), "no include may be sent for a non-reasoning model.");
        AssertEx.False(root.TryGetProperty("parallel_tool_calls", out _), "an unset parallel_tool_calls is left to the adapter.");
    }

    [Test]
    public async Task Stateless_WhenTheTypedReasoningDisagrees_TheBuildersEffortWins()
    {
        // The adapter maps ChatOptions.Reasoning only when the raw options carry no reasoning block of their own.
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.Low
            }
        };

        var root = await SendAsync(OpenAICompatibleResponsesRequest.Stateless(options,
            includeReasoning: true,
            ResponseReasoningEffortLevel.High,
            parallelToolCalls: null));

        AssertEx.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Test]
    public async Task Stateless_WithoutReasoning_LetsALeftoverTypedReasoningThrough()
    {
        // Why callers clear ChatOptions.Reasoning before building: with no raw reasoning block the adapter falls back to
        // the typed one, so a non-reasoning model would still be sent an effort.
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.Medium
            }
        };

        var root = await SendAsync(OpenAICompatibleResponsesRequest.Stateless(options,
            includeReasoning: false,
            effort: null,
            parallelToolCalls: null));

        AssertEx.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Test]
    public void Stateless_DecoratesTheGivenInstanceAndCreatesOneWhenNull()
    {
        var options = new ChatOptions();

        AssertEx.True(ReferenceEquals(options, OpenAICompatibleResponsesRequest.Stateless(options, includeReasoning: false, effort: null, parallelToolCalls: null)));
        AssertEx.NotNull(OpenAICompatibleResponsesRequest.Stateless(options: null, includeReasoning: false, effort: null, parallelToolCalls: null)
                                                         .RawRepresentationFactory);
    }

    private static async Task<JsonElement> SendAsync(ChatOptions options)
    {
        var recorder = new OpenAiWireRecorder
        {
            Responder = static _ => OpenAiWireRecorder.ResponsesResult("ok")
        };
#pragma warning disable CA2000 // The handler, HttpClient and transport all transfer to the chat client, which owns them.
        using var chat = OpenAICompatibleClientFactory.CreateResponsesChatClient(BaseAddress,
            "test-model",
            apiKey: null,
            TimeSpan.FromSeconds(30),
            new HttpClientPipelineTransport(new HttpClient(recorder.CreateHandler(), disposeHandler: true)));
#pragma warning restore CA2000

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);

        AssertEx.Equal("ok", response.Text);
        AssertEx.Equal("http://127.0.0.1:1/v1/responses", recorder.LastRequest.Uri?.AbsoluteUri);
        using var body = JsonDocument.Parse(AssertEx.NotNull(recorder.LastRequest.Body));
        return body.RootElement.Clone();
    }
}
#pragma warning restore OPENAI001
