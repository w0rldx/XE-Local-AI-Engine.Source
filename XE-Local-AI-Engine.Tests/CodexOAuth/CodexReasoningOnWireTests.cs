namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     On-WIRE assertions for the Codex reasoning display: the Responses request the SDK POSTs opts into summaries
///     (<c>reasoning.summary == "auto"</c>) and carries the per-send <c>reasoning.effort</c>, keeps store=false and
///     omits server-side state ids.
/// </summary>
/// <remarks>
///     HTTP is mocked, so this body shape is the correctness proof; the MEAI summary→TextReasoningContent mapping is
///     verified against a live model. The effort arrives via <see cref="ChatOptions.AdditionalProperties" />: the
///     Codex-only <c>codex_reasoning_effort</c> raw string (full fidelity, incl. minimal/xhigh) and the Ollama-shaped
///     <c>think</c> value (false / "low"/"medium"/"high" / true). Both channels run through the real SDK serialization
///     over a capturing transport.
/// </remarks>
[Category(TestCategories.Unit)]
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
        using var capture = new ResponsesBodyCapturingHandler();
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
}
