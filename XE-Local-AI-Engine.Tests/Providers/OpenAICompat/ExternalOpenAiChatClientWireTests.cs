namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompat;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end wire behavior of one external model's chat client, driven through the REAL production stack
///     (registry → endpoint guard → MEAI OpenAI chat-completions adapter → reasoning rewriting) over a recording
///     transport. Every assertion is on the serialized request that actually left the process, because the things being
///     pinned here — which header is present, which body field is set — are exactly the ones an options-level
///     assertion would report as correct while the assembled pipeline did something else.
/// </summary>
public sealed class ExternalOpenAiChatClientWireTests
{
    [Test]
    public async Task Send_UsesTheConnectionsWireIdAndNormalizedEndpoint()
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099"),
            ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:18099/v1/chat/completions", recorder.LastRequest.Uri?.AbsoluteUri);
        using var body = JsonDocument.Parse(AssertEx.NotNull(recorder.LastRequest.Body));
        // The RAW wire id goes on the wire — never the namespaced ext: id, which is a node-internal routing key.
        AssertEx.Equal(ExternalProviderTestData.WireId, body.RootElement.GetProperty("model").GetString());
    }

    [Test]
    public async Task Send_WithAConfiguredKey_CarriesTheBearerHeader_AndWithoutOneCarriesNoAuthorizationAtAll()
    {
        var keyed = new OpenAiWireRecorder();
        var keyedRegistry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(), apiKey: "sk-unsloth-abc");
        using (var client = new ExternalOpenAiChatClient(keyedRegistry, ExternalProviderTestData.ModelId, keyed.CreateHandler))
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        }

        var keyless = new OpenAiWireRecorder();
        var keylessRegistry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using (var client = new ExternalOpenAiChatClient(keylessRegistry, ExternalProviderTestData.ModelId, keyless.CreateHandler))
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        }

        AssertEx.Equal("Bearer sk-unsloth-abc", keyed.LastRequest.Authorization);
        AssertEx.False(keyless.LastRequest.HasAuthorizationHeader, "a keyless connection must send no Authorization header.");
    }

    [Test]
    [Arguments(true, "medium", null, "medium")]
    // The turn's selection beats the model's registered default.
    [Arguments(true, "medium", "low", "low")]
    // Clamped to the interoperable low|medium|high set: minimal->low, xhigh->high.
    [Arguments(true, null, "minimal", "low")]
    [Arguments(true, null, "xhigh", "high")]
    [Arguments(true, null, "high", "high")]
    public async Task Send_WhenTheModelDeclaresEffortSupport_SetsReasoningEffortOnTheBody(bool supportsEffort,
        string? defaultEffort,
        string? selectedEffort,
        string expected)
    {
        var body = await SendWithEffortAsync(supportsEffort, defaultEffort, selectedEffort);

        AssertEx.Equal(expected, body.RootElement.GetProperty("reasoning_effort").GetString());
        body.Dispose();
    }

    [Test]
    // No declared effort support: the field never rides, whatever the turn selected.
    [Arguments(false, "high", "high")]
    // Explicit off and the binary "reason by default" sentinel are NOT graded levels — omitting the field is not the
    // same as sending "none", which some servers read as an instruction of its own.
    [Arguments(true, null, "none")]
    [Arguments(true, null, "on")]
    // An unrecognized selection sends nothing rather than silently falling back to the registered default.
    [Arguments(true, "high", "bogus")]
    // Nothing selected and nothing declared.
    [Arguments(true, null, null)]
    public async Task Send_WhenNoGradedEffortApplies_OmitsTheFieldEntirely(bool supportsEffort, string? defaultEffort, string? selectedEffort)
    {
        var body = await SendWithEffortAsync(supportsEffort, defaultEffort, selectedEffort);

        AssertEx.False(body.RootElement.TryGetProperty("reasoning_effort", out _), "no graded effort applies, so the field must be absent.");
        body.Dispose();
    }

    [Test]
    public async Task Send_NeverSerializesTheInProcessEffortMarker()
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(supportsEffort: true));
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ExternalProviderConstants.ReasoningEffortMarkerKey] = "high"
            }
        };

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);

        AssertEx.False(AssertEx.NotNull(recorder.LastRequest.Body).Contains("xe.external", StringComparison.Ordinal),
            "the in-process marker must never reach the wire.");
    }

    [Test]
    public async Task SecondTurn_DoesNotReplayTheFirstTurnsReasoningToTheServer()
    {
        // v1 decision: reasoning is never replayed. Chat Completions drops historical TextReasoningContent by design
        // (the same behavior the llama.cpp path has today), and this test is what stops a future change from silently
        // starting to ship a model's private thinking back to a remote endpoint on every follow-up turn.
        var recorder = new OpenAiWireRecorder
        {
            Responder = static index => index == 0
                ? OpenAiWireRecorder.Completion("first answer", "\"reasoning_content\":\"PRIVATE-THOUGHT\"")
                : OpenAiWireRecorder.Completion("second answer")
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        AssertEx.Contains(first.Messages[0].Contents, static content => content is TextReasoningContent { Text: "PRIVATE-THOUGHT" });

        List<ChatMessage> history = [new ChatMessage(ChatRole.User, "hi"), .. first.Messages, new ChatMessage(ChatRole.User, "again")];
        _ = await client.GetResponseAsync(history, options: null, CancellationToken.None);

        var replayed = AssertEx.NotNull(recorder.LastRequest.Body);
        AssertEx.False(replayed.Contains("PRIVATE-THOUGHT", StringComparison.Ordinal),
            "the assistant's reasoning must not be replayed to the server on a later turn.");
        AssertEx.Contains(replayed, "first answer");
    }

    [Test]
    public async Task Send_AfterTheConnectionBaseUrlChanges_RebuildsAgainstTheNewEndpoint()
    {
        // An operator edit must take effect on the next send; a cached adapter bound to the old address would keep
        // talking to an endpoint they have already moved away from.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099"),
            ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18100"), ExternalProviderTestData.Model());
        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:18099/v1/chat/completions", recorder.Requests[0].Uri?.AbsoluteUri);
        AssertEx.Equal("http://127.0.0.1:18100/v1/chat/completions", recorder.Requests[1].Uri?.AbsoluteUri);
    }

    [Test]
    public async Task Send_WhenTheModelIsNoLongerRegistered_FailsClosedInsteadOfGuessingAnEndpoint()
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, "ext:deleted-box/some-model", recorder.CreateHandler);

        _ = await AssertEx.ThrowsAsync<ExternalProviderModelUnavailableException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Empty(recorder.Requests);
    }

    private static async Task<JsonDocument> SendWithEffortAsync(bool supportsEffort, string? defaultEffort, string? selectedEffort)
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(),
            ExternalProviderTestData.Model(supportsEffort, defaultEffort, supportsReasoning: true));
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        ChatOptions? options = null;
        if (selectedEffort is not null)
        {
            options = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [ExternalProviderConstants.ReasoningEffortMarkerKey] = selectedEffort
                }
            };
        }

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);
        return JsonDocument.Parse(AssertEx.NotNull(recorder.LastRequest.Body));
    }
}
