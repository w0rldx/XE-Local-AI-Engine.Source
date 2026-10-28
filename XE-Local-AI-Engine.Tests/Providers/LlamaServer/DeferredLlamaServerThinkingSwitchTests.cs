namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Reasoning-off must reach llama.cpp. The MEAI OpenAI adapter drops the Ollama <c>think:false</c>, so
///     <see cref="DeferredLlamaServerChatClient.ApplyThinkingSwitch" /> injects <c>enable_thinking=false</c> into
///     <c>chat_template_kwargs</c> via <c>ChatCompletionOptions.Patch</c> on the raw representation.
/// </summary>
/// <remarks>
///     Graded on the REAL pipeline's body: the switch appears ONLY under the disable-thinking marker.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class DeferredLlamaServerThinkingSwitchTests
{
    [Test]
    public async Task ApplyThinkingSwitch_MarkerPresent_InjectsEnableThinkingFalseIntoWireBody()
    {
        using var handler = new ChatCompletionCapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.DisableThinkingMarkerKey] = true
            }
        };
        var patched = DeferredLlamaServerChatClient.ApplyThinkingSwitch(options);

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], patched, CancellationToken.None);

        var body = AssertEx.NotNull(handler.CapturedBody);
        AssertEx.Contains(body, "chat_template_kwargs", StringComparison.Ordinal);
        AssertEx.Contains(body, "enable_thinking", StringComparison.Ordinal);
        // Parse to assert the actual value rather than substring-matching a coincidental token.
        using var doc = JsonDocument.Parse(body);
        var kwargs = doc.RootElement.GetProperty("chat_template_kwargs");
        AssertEx.Equal(expected: false, kwargs.GetProperty("enable_thinking").GetBoolean());

        // The zero budget is the off switch for a template that renders <think> but never reads enable_thinking (LFM2.5).
        AssertEx.Equal(expected: 0, doc.RootElement.GetProperty("reasoning_budget_tokens").GetInt32());
    }

    [Test]
    public async Task ApplyThinkingSwitch_MarkerAbsent_BodyHasNoChatTemplateKwargs()
    {
        using var handler = new ChatCompletionCapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        // No marker → ApplyThinkingSwitch returns the options unchanged and nothing is injected.
        var options = new ChatOptions();
        var passthrough = DeferredLlamaServerChatClient.ApplyThinkingSwitch(options);
        AssertEx.True(ReferenceEquals(options, passthrough), "with no marker the options must be returned unchanged (byte-identical path).");

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], passthrough, CancellationToken.None);

        var body = AssertEx.NotNull(handler.CapturedBody);
        AssertEx.False(body.Contains("chat_template_kwargs", StringComparison.Ordinal),
            "a request without the disable-thinking marker must never carry chat_template_kwargs.");
        AssertEx.False(body.Contains("reasoning_budget_tokens", StringComparison.Ordinal),
            "a request without the disable-thinking marker must never carry a zero reasoning budget.");
    }

    private static IChatClient BuildOpenAiChatClient(HttpClient http)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("http://127.0.0.1:1/v1"),
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };
        var client = new OpenAIClient(new ApiKeyCredential("ignored"), options);
        return client.GetChatClient("test-model").AsIChatClient();
    }
}
