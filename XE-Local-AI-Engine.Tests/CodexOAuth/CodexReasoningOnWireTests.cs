namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     On-WIRE reasoning-summary + reasoning-effort assertions for the Codex reasoning display. Proves the Codex
///     Responses request the SDK actually POSTs opts into reasoning summaries (<c>reasoning.summary == "auto"</c>)
///     and carries the per-send effort (<c>reasoning.effort</c>) mapped from the chat reasoning
///     effort, WHILE preserving the store=false invariant and omitting server-side state ids. HTTP is mocked, so this
///     body-shape assertion is the correctness proof; the MEAI summary→TextReasoningContent mapping is verified
///     separately against a live model.
///     <para>
///         The effort reaches the Codex boundary via <see cref="ChatOptions.AdditionalProperties" />: the agent factory sets
///         the Codex-only <c>codex_reasoning_effort</c> raw-string side channel for a thinking-capable model (full fidelity,
///         incl. minimal/xhigh) and the Ollama-shaped <c>think</c> value (false / "low"/"medium"/"high" / true). These tests
///         drive both channels through the real SDK serialization over a capturing transport.
///     </para>
/// </summary>
public sealed class CodexReasoningOnWireTests
{
    private const string CodexReasoningEffortKey = "codex_reasoning_effort";

    [Test]
    public async Task CodexResponsesRequest_AlwaysRequestsAutoReasoningSummary_AndKeepsStoreFalse()
    {
        // No effort supplied (think:true ≡ reason at default effort): summaries are still requested (summary=auto) and
        // the effort field is omitted so the model's default effort applies. store=false / no server state preserved.
        var body = await CaptureRequestBodyAsync(new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = true
            }
        });

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        var reasoning = AssertReasoningObject(root);
        AssertEx.True(reasoning.TryGetProperty("summary", out var summary), "reasoning.summary must be requested");
        AssertEx.Equal("auto", summary.GetString());
        AssertEx.False(reasoning.TryGetProperty("effort", out _), "reasoning.effort must be omitted when effort is unspecified");

        AssertStoreFalseWithoutServerState(root);
    }

    [Test]
    [Arguments("none", "none")]
    [Arguments("minimal", "minimal")]
    [Arguments("low", "low")]
    [Arguments("medium", "medium")]
    [Arguments("high", "high")]
    // xhigh has no XHigh member in OpenAI 2.10.0 → degrades to the nearest supported level (high) on the wire.
    [Arguments("xhigh", "high")]
    public async Task CodexResponsesRequest_MapsSideChannelEffort_ToReasoningEffortOnWire(string effort, string expectedWire)
    {
        // The Codex side channel carries the raw normalized effort; it is preferred over `think` and supports the full
        // OpenAI Responses set (minimal/xhigh) that the Ollama `think` value cannot carry.
        var properties = new AdditionalPropertiesDictionary
        {
            // `think` rides alongside (factory sets both); the side channel must win.
            ["think"] = true,
            [CodexReasoningEffortKey] = effort
        };

        var body = await CaptureRequestBodyAsync(new ChatOptions
        {
            AdditionalProperties = properties
        });

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        var reasoning = AssertReasoningObject(root);
        AssertEx.True(reasoning.TryGetProperty("summary", out var summary), "reasoning.summary must be requested");
        AssertEx.Equal("auto", summary.GetString());
        AssertEx.True(reasoning.TryGetProperty("effort", out var wireEffort), "reasoning.effort must be set");
        AssertEx.Equal(expectedWire, wireEffort.GetString());

        AssertStoreFalseWithoutServerState(root);
    }

    [Test]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    public async Task CodexResponsesRequest_FallsBackToThinkLevel_WhenSideChannelAbsent(string level)
    {
        // Without the side channel, the graded Ollama `think` level still maps to reasoning.effort (back-compat with the
        // think-only carry; covers low/medium/high which `think` CAN carry).
        var body = await CaptureRequestBodyAsync(new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = level
            }
        });

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        var reasoning = AssertReasoningObject(root);
        AssertEx.True(reasoning.TryGetProperty("effort", out var wireEffort), "reasoning.effort must be set from think level");
        AssertEx.Equal(level, wireEffort.GetString());

        AssertStoreFalseWithoutServerState(root);
    }

    [Test]
    public async Task CodexResponsesRequest_WhenThinkFalse_SerializesReasoningEffortNone()
    {
        // Reasoning OFF: think:false ≡ effort none. We still request summaries (harmless; the model emits none at none),
        // and the store=false invariant holds.
        var body = await CaptureRequestBodyAsync(new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = false
            }
        });

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        var reasoning = AssertReasoningObject(root);
        AssertEx.True(reasoning.TryGetProperty("effort", out var wireEffort), "reasoning.effort must be 'none' when think is false");
        AssertEx.Equal("none", wireEffort.GetString());

        AssertStoreFalseWithoutServerState(root);
    }

    private static JsonElement AssertReasoningObject(JsonElement root)
    {
        AssertEx.True(root.TryGetProperty("reasoning", out var reasoning), "request body must contain a 'reasoning' object");
        AssertEx.Equal(JsonValueKind.Object, reasoning.ValueKind);
        return reasoning;
    }

    private static void AssertStoreFalseWithoutServerState(JsonElement root)
    {
        AssertEx.True(root.TryGetProperty("store", out var store), "request body must contain a 'store' field");
        AssertEx.Equal(JsonValueKind.False, store.ValueKind);
        AssertEx.False(root.TryGetProperty("previous_response_id", out _), "must NOT send previous_response_id");
        AssertEx.False(root.TryGetProperty("conversation", out _), "must NOT send conversation");
    }

    private static async Task<string> CaptureRequestBodyAsync(ChatOptions options)
    {
        using var capture = new BodyCapturingHandler();
        using var httpClient = new HttpClient(capture);

        var inner = CodexChatClientConstruction.Build(new Uri("https://chatgpt.com/backend-api/codex"),
            httpClient,
            "gpt-5.4");
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");

        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);
        }
        catch (Exception)
        {
            // The canned response is a minimal stub the SDK may fail to fully deserialize; only the captured REQUEST
            // body (recorded before any response parsing) matters here.
        }

        return AssertEx.NotNull(capture.RequestBody, "the Responses request body must have been sent");
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

            const string CannedResponse =
                """{"id":"resp_test","object":"response","status":"completed","model":"gpt-5.4","output":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedResponse, Encoding.UTF8, "application/json")
            };
        }
    }
}
