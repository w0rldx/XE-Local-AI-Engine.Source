namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     On-WIRE store=false assertion. The <see cref="CodexStoreDisabledChatClientTests" /> proves the
///     flag on the request options; this proves it on the actual SERIALIZED request body the OpenAI Responses client
///     POSTs — driving the real Responses-backed <see cref="IChatClient" /> over a capturing transport and asserting
///     the JSON contains <c>"store": false</c> AND omits <c>previous_response_id</c> / <c>conversation</c> (no
///     service-side state retention).
/// </summary>
public sealed class CodexStoreFalseOnWireTests
{
    [Test]
    public async Task CodexResponsesRequest_SerializesStoreFalse_AndOmitsServerStateIds()
    {
        using var capture = new BodyCapturingHandler();
        using var httpClient = new HttpClient(capture);

        // Build the SAME Responses-backed IChatClient the factory builds (real SDK serialization), wrapped in the
        // store-disabling decorator, but pointed at our capturing transport instead of the live Codex backend.
        var inner = CodexChatClientConstruction.Build(new Uri("https://chatgpt.com/backend-api/codex"),
            httpClient,
            "gpt-5.4");
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");

        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }
        catch (Exception)
        {
            // The canned response is a minimal stub; the SDK may not fully deserialize it. We only care that the
            // REQUEST was serialized + sent — which the capturing handler recorded before any response parsing.
        }

        var body = AssertEx.NotNull(capture.RequestBody, "the Responses request body must have been sent");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // store=false reached the wire.
        AssertEx.True(root.TryGetProperty("store", out var store), "request body must contain a 'store' field");
        AssertEx.Equal(JsonValueKind.False, store.ValueKind);

        // No server-side conversation state is referenced.
        AssertEx.False(root.TryGetProperty("previous_response_id", out _), "must NOT send previous_response_id");
        AssertEx.False(root.TryGetProperty("conversation", out _), "must NOT send conversation");
    }

    /// <summary>
    ///     400-regression: the agent send path sets <see cref="ChatOptions.ModelId" /> to the node's LOCAL model
    ///     (e.g. <c>qwen3:8b</c>). The Codex wrapper must OVERWRITE that with the resolved Codex model id so only a
    ///     valid Codex model reaches the wire — proving the leaked local name can never be the request model (which was
    ///     the cause of the live HTTP 400 from chatgpt.com/backend-api/codex/responses).
    /// </summary>
    [Test]
    public async Task CodexResponsesRequest_WhenLocalModelIdSupplied_SerializesPinnedCodexModelOnWire()
    {
        using var capture = new BodyCapturingHandler();
        using var httpClient = new HttpClient(capture);

        var inner = CodexChatClientConstruction.Build(new Uri("https://chatgpt.com/backend-api/codex"),
            httpClient,
            "gpt-5.4");
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");

        try
        {
            // Simulate the agent send path leaking a LOCAL Ollama model name via ChatOptions.ModelId.
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
                new ChatOptions
                {
                    ModelId = "qwen3:8b"
                });
        }
        catch (Exception)
        {
            // Only the serialized request matters; the canned response may not fully deserialize.
        }

        var body = AssertEx.NotNull(capture.RequestBody, "the Responses request body must have been sent");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        AssertEx.True(root.TryGetProperty("model", out var model), "request body must contain a 'model' field");
        AssertEx.Equal("gpt-5.4", model.GetString());
        AssertEx.NotEqual("qwen3:8b", model.GetString());
    }

    /// <summary>
    ///     System-message 400 regression: the ChatGPT-subscription Codex backend rejects system-role messages in the
    ///     request input (<c>{"detail":"System messages are not allowed"}</c>). The Codex wrapper must move every
    ///     system message's text into the top-level Responses <c>instructions</c> field and send NO system message in
    ///     the input. This asserts both on the serialized wire body.
    /// </summary>
    [Test]
    public async Task CodexResponsesRequest_WhenSystemMessageSupplied_MovesItToInstructions_AndOmitsFromInput()
    {
        using var capture = new BodyCapturingHandler();
        using var httpClient = new HttpClient(capture);

        var inner = CodexChatClientConstruction.Build(new Uri("https://chatgpt.com/backend-api/codex"),
            httpClient,
            "gpt-5.4");
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");

        const string SystemPrompt = "You are a helpful XE assistant.";
        try
        {
            await client.GetResponseAsync([
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, "hi")
            ]);
        }
        catch (Exception)
        {
            // Only the serialized request matters; the canned response may not fully deserialize.
        }

        var body = AssertEx.NotNull(capture.RequestBody, "the Responses request body must have been sent");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // The system prompt rides the top-level `instructions` field (allowed by the Codex backend).
        AssertEx.True(root.TryGetProperty("instructions", out var instructions), "request body must contain an 'instructions' field");
        AssertEx.Equal(JsonValueKind.String, instructions.ValueKind);
        AssertEx.True(instructions.GetString()?.Contains(SystemPrompt, StringComparison.Ordinal) == true,
            "the system prompt text must be carried in 'instructions'");

        // NO input item carries role "system" — the rejected shape never reaches the wire.
        AssertEx.True(root.TryGetProperty("input", out var input), "request body must contain an 'input' field");
        var hasSystemInput = input.ValueKind == JsonValueKind.Array
                             && input.EnumerateArray().Any(static item =>
                                 item.TryGetProperty("role", out var role)
                                 && string.Equals(role.GetString(), "system", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(hasSystemInput, "the Codex request input must NOT contain a system-role message");
    }

    /// <summary>Captures the outbound request body and returns a minimal canned Responses reply.</summary>
    private sealed class BodyCapturingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            // Minimal Responses-shaped JSON so the SDK gets a 200 with a body to (attempt to) parse.
            const string CannedResponse =
                """{"id":"resp_test","object":"response","status":"completed","model":"gpt-5.4","output":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedResponse, Encoding.UTF8, "application/json")
            };
        }
    }
}
