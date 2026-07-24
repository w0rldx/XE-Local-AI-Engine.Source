namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Reasoning-off must actually reach llama.cpp. The Ollama <c>think:false</c> the factory writes is
///     dropped by the MEAI OpenAI adapter, so <see cref="DeferredLlamaServerChatClient.ApplyThinkingSwitch" /> injects
///     <c>chat_template_kwargs.enable_thinking=false</c> via <c>ChatCompletionOptions.Patch</c> (through
///     <see cref="ChatOptions.RawRepresentationFactory" />) so it rides the wire. These tests assemble the REAL MEAI
///     OpenAI chat pipeline over a request-capturing transport and assert the serialized body: the switch is present ONLY
///     when the disable-thinking marker is set, and every other request is byte-identical (no <c>chat_template_kwargs</c>).
/// </summary>
public sealed class DeferredLlamaServerThinkingSwitchTests
{
    [Test]
    public async Task ApplyThinkingSwitch_MarkerPresent_InjectsEnableThinkingFalseIntoWireBody()
    {
        using var handler = new CapturingHandler();
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
    }

    [Test]
    public async Task ApplyThinkingSwitch_MarkerAbsent_BodyHasNoChatTemplateKwargs()
    {
        using var handler = new CapturingHandler();
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

    /// <summary>Captures the outbound request body and returns a canned OpenAI chat completion so no network is hit.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string CannedCompletion =
            "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"test-model\","
            + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedCompletion, Encoding.UTF8, "application/json")
            };
        }
    }
}
