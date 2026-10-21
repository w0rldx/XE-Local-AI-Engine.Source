namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompat;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A model declared on the Responses surface, driven through the real external-provider stack over a recording
///     transport: which endpoint it hits, that every send is stateless, and how the declared reasoning reaches the body.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ExternalOpenAiResponsesWireTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Send_OnAResponsesModel_PostsToResponsesStatelesslyWithTheWireId(bool withOptions)
    {
        // Null options must still go out stateless: the adapter only applies the raw factory when options exist.
        var recorder = ResponsesRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(),
            ExternalProviderTestData.Model(apiSurface: ExternalProviderApiSurface.Responses));
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);
        var options = withOptions
            ? new ChatOptions
            {
                ModelId = ExternalProviderTestData.ModelId
            }
            : null;

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);

        AssertEx.Equal("ok", response.Text);
        AssertEx.Equal("http://127.0.0.1:18099/v1/responses", recorder.LastRequest.Uri?.AbsoluteUri);
        using var body = JsonDocument.Parse(AssertEx.NotNull(recorder.LastRequest.Body));
        AssertEx.Equal(ExternalProviderTestData.WireId, body.RootElement.GetProperty("model").GetString());
        AssertEx.Equal(JsonValueKind.False, body.RootElement.GetProperty("store").ValueKind);
        AssertEx.False(body.RootElement.TryGetProperty("previous_response_id", out _), "a stateless send must not reference a stored response.");
    }

    [Test]
    [Arguments(null, "medium", "medium")]
    // The turn's selection beats the default, clamped to low|medium|high exactly as on Chat Completions.
    [Arguments("low", "high", "low")]
    [Arguments("xhigh", null, "high")]
    [Arguments("minimal", null, "low")]
    public async Task Send_WhenTheModelDeclaresEffort_SendsItAsReasoningEffortWithTheSummaryAndInclude(string? selected,
        string? defaultEffort,
        string expected)
    {
        var root = await SendAsync(ExternalProviderTestData.Model(supportsEffort: true,
                defaultEffort: defaultEffort,
                supportsReasoning: true,
                apiSurface: ExternalProviderApiSurface.Responses),
            selected);

        var reasoning = root.GetProperty("reasoning");
        AssertEx.Equal(expected, reasoning.GetProperty("effort").GetString());
        AssertEx.Equal("auto", reasoning.GetProperty("summary").GetString());
        AssertEx.Contains(root.GetProperty("include").EnumerateArray().Select(static item => item.GetString()), "reasoning.encrypted_content");
        AssertEx.False(root.TryGetProperty("reasoning_effort", out _), "the Chat Completions field must not ride a Responses body.");
    }

    [Test]
    public async Task Send_WhenTheModelReasonsWithoutEffort_RequestsTheSummaryButNoEffort()
    {
        var root = await SendAsync(ExternalProviderTestData.Model(supportsReasoning: true, apiSurface: ExternalProviderApiSurface.Responses),
            selectedEffort: "high");

        var reasoning = root.GetProperty("reasoning");
        AssertEx.Equal("auto", reasoning.GetProperty("summary").GetString());
        AssertEx.False(reasoning.TryGetProperty("effort", out _), "no effort may be sent for a model that declares no graded effort.");
    }

    [Test]
    public async Task Send_WhenTheModelDeclaresNoReasoning_SendsNoReasoningBlockAndNoInclude()
    {
        var root = await SendAsync(ExternalProviderTestData.Model(apiSurface: ExternalProviderApiSurface.Responses), selectedEffort: "high");

        AssertEx.Equal(JsonValueKind.False, root.GetProperty("store").ValueKind);
        AssertEx.False(root.TryGetProperty("reasoning", out _), "a non-reasoning model must not be sent a reasoning block.");
        AssertEx.False(root.TryGetProperty("include", out _), "a non-reasoning model must not be sent the encrypted-reasoning include.");
    }

    [Test]
    public async Task Send_AfterTheModelsSurfaceIsFlipped_RebuildsAgainstTheOtherEndpoint()
    {
        var recorder = new OpenAiWireRecorder
        {
            Responder = static index => index == 0 ? OpenAiWireRecorder.Completion("ok") : OpenAiWireRecorder.ResponsesResult("ok")
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(apiSurface: ExternalProviderApiSurface.Responses));
        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:18099/v1/chat/completions", recorder.Requests[0].Uri?.AbsoluteUri);
        AssertEx.Equal("http://127.0.0.1:18099/v1/responses", recorder.Requests[1].Uri?.AbsoluteUri);
    }

    [Test]
    public async Task Send_OnASurfaceWithNoChatClient_ThrowsAndSendsNothing()
    {
        var recorder = ResponsesRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(),
            ExternalProviderTestData.Model(apiSurface: (ExternalProviderApiSurface)2));
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await AssertEx.ThrowsAsync<NotSupportedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Empty(recorder.Requests);
    }

    private static OpenAiWireRecorder ResponsesRecorder()
    {
        return new OpenAiWireRecorder
        {
            Responder = static _ => OpenAiWireRecorder.ResponsesResult("ok")
        };
    }

    private static async Task<JsonElement> SendAsync(ExternalProviderModelDescriptor model, string? selectedEffort)
    {
        var recorder = ResponsesRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), model);
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);
        var options = new ChatOptions();
        if (selectedEffort is not null)
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ExternalProviderConstants.ReasoningEffortMarkerKey] = selectedEffort
            };
        }

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);

        using var body = JsonDocument.Parse(AssertEx.NotNull(recorder.LastRequest.Body));
        return body.RootElement.Clone();
    }
}
