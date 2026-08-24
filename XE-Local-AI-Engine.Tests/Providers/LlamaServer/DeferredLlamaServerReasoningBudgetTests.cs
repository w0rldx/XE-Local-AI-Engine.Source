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
///     The reasoning cap must actually reach llama.cpp. A thinking model with no budget runs its reasoning until the
///     context window is exhausted and returns no final answer, and the MEAI OpenAI adapter drops unmapped
///     <see cref="ChatOptions.AdditionalProperties" />, so
///     <see cref="DeferredLlamaServerChatClient.ApplyReasoningBudget" /> patches <c>reasoning_budget_tokens</c> onto the
///     body via <c>ChatCompletionOptions.Patch</c> (through <see cref="ChatOptions.RawRepresentationFactory" />). These
///     tests assemble the REAL MEAI OpenAI chat pipeline over a request-capturing transport and assert the serialized
///     body: the budget is present with the marker's value, and absent — byte-identical — without it.
/// </summary>
public sealed class DeferredLlamaServerReasoningBudgetTests
{
    [Test]
    [Arguments(2048)]
    [Arguments(8192)]
    [Arguments(24576)]
    public async Task ApplyReasoningBudget_MarkerPresent_InjectsTheBudgetIntoWireBody(int budgetTokens)
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.ReasoningBudgetMarkerKey] = budgetTokens
            }
        };
        var patched = DeferredLlamaServerChatClient.ApplyReasoningBudget(options);

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], patched, CancellationToken.None);

        var body = AssertEx.NotNull(handler.CapturedBody);
        using var doc = JsonDocument.Parse(body);
        AssertEx.Equal(budgetTokens, doc.RootElement.GetProperty("reasoning_budget_tokens").GetInt32());
    }

    [Test]
    public async Task ApplyReasoningBudget_MarkerAbsent_BodyHasNoBudget()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        // No marker → ApplyReasoningBudget returns the options unchanged and nothing is injected. This is the shape of
        // every request with no explicit reasoning effort (and of every non-thinking model), which must keep the
        // unrestricted pre-budget behavior.
        var options = new ChatOptions();
        var passthrough = DeferredLlamaServerChatClient.ApplyReasoningBudget(options);
        AssertEx.True(ReferenceEquals(options, passthrough), "with no marker the options must be returned unchanged (byte-identical path).");

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], passthrough, CancellationToken.None);

        var body = AssertEx.NotNull(handler.CapturedBody);
        AssertEx.False(body.Contains("reasoning_budget", StringComparison.Ordinal),
            "a request without the budget marker must never carry a reasoning budget.");
    }

    [Test]
    public async Task ReasoningBudgetMarker_WithoutThisClient_NeverReachesTheWire()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var chat = BuildOpenAiChatClient(http);

        // The marker is in-process only: it is a plain AdditionalProperties entry, which the MEAI OpenAI adapter drops.
        // Any provider that does not run ApplyReasoningBudget (cloud OpenAI-compatible endpoints, and — via their own
        // fixed option allowlists — Ollama and Codex) therefore sends a body with no budget field at all.
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.ReasoningBudgetMarkerKey] = 8192
            }
        };

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);

        var body = AssertEx.NotNull(handler.CapturedBody);
        AssertEx.False(body.Contains("reasoning_budget", StringComparison.Ordinal),
            "the budget must ride the wire only through ApplyReasoningBudget.");
        AssertEx.False(body.Contains("xe.llama", StringComparison.Ordinal),
            "the in-process marker itself must never be serialized.");
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
