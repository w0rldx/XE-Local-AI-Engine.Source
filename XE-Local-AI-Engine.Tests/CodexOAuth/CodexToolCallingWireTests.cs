namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     On-WIRE tool-calling assertions for the Codex (ChatGPT-subscription) Responses transport. These drive the SAME
///     production <see cref="CodexStoreDisabledChatClient" /> wrapper the live send path uses (NOT the inner client
///     directly), over the <c>BodyCapturingHandler</c> + real Responses-backed <see cref="IChatClient" /> harness the
///     sibling <see cref="CodexReasoningOnWireTests" /> / <see cref="CodexStoreFalseOnWireTests" /> use, and assert the
///     two requests the stateless tool loop depends on serialize correctly:
///     <list type="number">
///         <item>
///             #1 first turn: a function tool reaches <c>tools[]</c>, the wrapper adds
///             <c>include:[reasoning.encrypted_content]</c> (required for the stateless tool loop), reasoning summary/effort
///             ride, <c>store=false</c>, no server-side state ids — and tools are NOT stripped by the system-message handling.
///         </item>
///         <item>
///             #2 follow-up turn: a prior reasoning item (carrying <c>encrypted_content</c>) + function call + tool output
///             round-trip back as input — reasoning BEFORE the function_call — via MEAI's
///             <c>RawRepresentation is ResponseItem</c> verbatim-replay path, still <c>store=false</c>.
///         </item>
///     </list>
///     The canned HTTP response is a minimal stub the SDK may fail to fully parse; only the captured REQUEST body
///     (recorded before any response parsing) is asserted, exactly like the sibling wire tests.
///     <para>
///         OPENAI001 is suppressed file-wide: the Responses options/items surface
///         (<see cref="CreateResponseOptions" />, <see cref="IncludedResponseProperty" />, <see cref="ReasoningResponseItem" />,
///         <see cref="ResponseItem" />) is experimental and the Tests project does not put it in <c>NoWarn</c> (unlike the
///         provider project).
///     </para>
/// </summary>
#pragma warning disable OPENAI001 // Experimental OpenAI Responses surface — the entire Codex transport is built on it.
public sealed class CodexToolCallingWireTests
{
    private const string ToolName = "get_weather";
    private const string CallId = "call_spike_1";
    private const string EncryptedReasoning = "gAAAAAB-encrypted-reasoning-blob";
    private const string ReasoningSummary = "I should look up the weather.";

    /// <summary>
    ///     #1 — Driving the production wrapper: a function tool reaches <c>tools[]</c>, the wrapper adds
    ///     <c>include:[reasoning.encrypted_content]</c>, reasoning summary/effort ride, store=false, no server-side state
    ///     ids — proving the include + tools serialize through the real Codex boundary, not just hand-built base options.
    /// </summary>
    [Test]
    public async Task FirstTurn_WithTool_WrapperAddsEncryptedReasoningInclude_AndKeepsTool()
    {
        var weatherTool = AIFunctionFactory.Create((string city) => $"sunny in {city}",
            ToolName,
            "Gets the current weather for a city.");

        var options = new ChatOptions
        {
            Tools = [weatherTool],
            // The wrapper resolves reasoning effort from AdditionalProperties; supply a graded level so the wire shows
            // reasoning.effort. The include + store=false are applied by the wrapper itself (not by the test).
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = "medium"
            }
        };

        var body = await CaptureRequestBodyAsync([new ChatMessage(ChatRole.User, "weather in Berlin?")], options);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // tools[] carries the function by name (tools are NOT stripped on the Codex boundary).
        AssertEx.True(root.TryGetProperty("tools", out var tools), "request body must contain a 'tools' array");
        AssertEx.Equal(JsonValueKind.Array, tools.ValueKind);
        var hasFunctionTool = tools.EnumerateArray().Any(static tool =>
            tool.TryGetProperty("name", out var name) && string.Equals(name.GetString(), ToolName, StringComparison.Ordinal));
        AssertEx.True(hasFunctionTool, $"tools[] must contain the '{ToolName}' function");

        // The wrapper added include:[reasoning.encrypted_content] (required for the stateless tool loop).
        AssertEx.True(root.TryGetProperty("include", out var include), "request body must contain an 'include' array");
        AssertEx.Equal(JsonValueKind.Array, include.ValueKind);
        var hasEncryptedInclude = include.EnumerateArray().Any(static entry =>
            string.Equals(entry.GetString(), "reasoning.encrypted_content", StringComparison.Ordinal));
        AssertEx.True(hasEncryptedInclude, "include[] must contain \"reasoning.encrypted_content\"");

        // reasoning.summary == auto, reasoning.effort == medium (from the think level the wrapper resolved).
        AssertEx.True(root.TryGetProperty("reasoning", out var reasoning), "request body must contain a 'reasoning' object");
        AssertEx.Equal(JsonValueKind.Object, reasoning.ValueKind);
        AssertEx.True(reasoning.TryGetProperty("summary", out var summary), "reasoning.summary must be set");
        AssertEx.Equal("auto", summary.GetString());
        AssertEx.True(reasoning.TryGetProperty("effort", out var effort), "reasoning.effort must be set");
        AssertEx.Equal("medium", effort.GetString());

        // Single-call-first: parallel tool calls disabled on the wire.
        AssertEx.True(root.TryGetProperty("parallel_tool_calls", out var parallelToolCalls), "request body must contain 'parallel_tool_calls'");
        AssertEx.Equal(JsonValueKind.False, parallelToolCalls.ValueKind);

        // store == false, no server-side state ids.
        AssertEx.True(root.TryGetProperty("store", out var store), "request body must contain a 'store' field");
        AssertEx.Equal(JsonValueKind.False, store.ValueKind);
        AssertEx.False(root.TryGetProperty("previous_response_id", out _), "must NOT send previous_response_id");
        AssertEx.False(root.TryGetProperty("conversation", out _), "must NOT send conversation");
    }

    /// <summary>
    ///     #1b — The include + tools survive the system-message strip: when a system message is present (moved into
    ///     top-level <c>instructions</c> by the wrapper), the tools and the encrypted-reasoning include still reach the
    ///     wire. Guards against the system-strip disturbing tool/reasoning items.
    /// </summary>
    [Test]
    public async Task FirstTurn_WithToolAndSystemMessage_KeepsToolsAndInclude_AndMovesSystemToInstructions()
    {
        var weatherTool = AIFunctionFactory.Create((string city) => $"sunny in {city}",
            ToolName,
            "Gets the current weather for a city.");

        const string SystemPrompt = "You are a helpful XE assistant.";
        var options = new ChatOptions
        {
            Tools = [weatherTool],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = true
            }
        };

        var body = await CaptureRequestBodyAsync([
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, "weather in Berlin?")
        ], options);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // Tools survive the system-message handling.
        AssertEx.True(root.TryGetProperty("tools", out var tools), "request body must contain a 'tools' array");
        var hasFunctionTool = tools.EnumerateArray().Any(static tool =>
            tool.TryGetProperty("name", out var name) && string.Equals(name.GetString(), ToolName, StringComparison.Ordinal));
        AssertEx.True(hasFunctionTool, $"tools[] must still contain the '{ToolName}' function");

        // The encrypted-reasoning include survives.
        AssertEx.True(root.TryGetProperty("include", out var include), "request body must contain an 'include' array");
        var hasEncryptedInclude = include.EnumerateArray().Any(static entry =>
            string.Equals(entry.GetString(), "reasoning.encrypted_content", StringComparison.Ordinal));
        AssertEx.True(hasEncryptedInclude, "include[] must still contain \"reasoning.encrypted_content\"");

        // The system prompt was moved to instructions; no system-role input item remains.
        AssertEx.True(root.TryGetProperty("instructions", out var instructions), "request body must contain 'instructions'");
        AssertEx.True(instructions.GetString()?.Contains(SystemPrompt, StringComparison.Ordinal) == true,
            "the system prompt must be carried in 'instructions'");
        AssertEx.True(root.TryGetProperty("input", out var input), "request body must contain an 'input' array");
        var hasSystemInput = input.ValueKind == JsonValueKind.Array
                             && input.EnumerateArray().Any(static item =>
                                 item.TryGetProperty("role", out var role)
                                 && string.Equals(role.GetString(), "system", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(hasSystemInput, "no system-role item may remain in input");
    }

    /// <summary>
    ///     #2 — A prior reasoning item (carrying <c>encrypted_content</c>), the function call, and the tool output all
    ///     round-trip back as the request input array, with the reasoning item BEFORE the function_call and store=false.
    ///     This is the stateless-replay mechanism: MEAI re-emits any content whose <c>RawRepresentation is ResponseItem</c>
    ///     verbatim, so the encrypted reasoning + function-call items minted here reappear unchanged on the wire — driven
    ///     through the production wrapper.
    /// </summary>
    [Test]
    public async Task FollowUpTurn_RoundTripsEncryptedReasoningAndFunctionItems()
    {
        var functionArguments = BinaryData.FromString("""{"city":"Berlin"}""");

        // A reasoning item carrying encrypted_content + a summary part — minted via the public SDK ctor + setter.
        var reasoningItem = new ReasoningResponseItem(ReasoningSummary)
        {
            EncryptedContent = EncryptedReasoning
        };

        // The function-call item the model emitted, matching the call id of the tool result below.
        var functionCallItem = ResponseItem.CreateFunctionCallItem(CallId, ToolName, functionArguments);

        var assistantMessage = new ChatMessage(ChatRole.Assistant,
        [
            // Order matters: reasoning BEFORE the function call (Codex requires the reasoning item to immediately
            // precede the function_call it produced). MEAI yields contents in order.
            new TextReasoningContent(ReasoningSummary)
            {
                RawRepresentation = reasoningItem
            },
            new FunctionCallContent(CallId, ToolName, new Dictionary<string, object?>
            {
                ["city"] = "Berlin"
            })
            {
                RawRepresentation = functionCallItem
            }
        ]);

        var toolMessage = new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent(CallId, "sunny in Berlin")
        ]);

        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "do X"),
            assistantMessage,
            toolMessage
        };

        var body = await CaptureRequestBodyAsync(messages,
            new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["think"] = true
                }
            });

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        AssertEx.True(root.TryGetProperty("input", out var input), "request body must contain an 'input' array");
        AssertEx.Equal(JsonValueKind.Array, input.ValueKind);
        var items = input.EnumerateArray().ToList();

        var reasoningIndex = items.FindIndex(static item =>
            item.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "reasoning", StringComparison.Ordinal));
        var functionCallIndex = items.FindIndex(static item =>
            item.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "function_call", StringComparison.Ordinal));
        var functionOutputIndex = items.FindIndex(static item =>
            item.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "function_call_output", StringComparison.Ordinal));

        // All three items re-emitted as input.
        AssertEx.True(reasoningIndex >= 0, "input[] must re-emit the reasoning item");
        AssertEx.True(functionCallIndex >= 0, "input[] must re-emit the function_call item");
        AssertEx.True(functionOutputIndex >= 0, "input[] must re-emit the function_call_output item");

        // The reasoning item carries its encrypted_content verbatim.
        var reasoningJson = items[reasoningIndex];
        AssertEx.True(reasoningJson.TryGetProperty("encrypted_content", out var encrypted),
            "the round-tripped reasoning item must carry 'encrypted_content'");
        AssertEx.Equal(EncryptedReasoning, encrypted.GetString());

        // The function_call carries the call id + name.
        var functionCallJson = items[functionCallIndex];
        AssertEx.True(functionCallJson.TryGetProperty("call_id", out var emittedCallId), "function_call must carry 'call_id'");
        AssertEx.Equal(CallId, emittedCallId.GetString());
        AssertEx.True(functionCallJson.TryGetProperty("name", out var emittedName), "function_call must carry 'name'");
        AssertEx.Equal(ToolName, emittedName.GetString());

        // The function_call_output matches the same call id.
        var functionOutputJson = items[functionOutputIndex];
        AssertEx.True(functionOutputJson.TryGetProperty("call_id", out var outputCallId), "function_call_output must carry 'call_id'");
        AssertEx.Equal(CallId, outputCallId.GetString());

        // Reasoning appears BEFORE the function_call (the Codex ordering invariant).
        AssertEx.True(reasoningIndex < functionCallIndex, "the reasoning item must appear before the function_call in input[]");

        // store=false invariant preserved on the follow-up turn.
        AssertEx.True(root.TryGetProperty("store", out var store), "request body must contain a 'store' field");
        AssertEx.Equal(JsonValueKind.False, store.ValueKind);
    }

    private static async Task<string> CaptureRequestBodyAsync(IEnumerable<ChatMessage> messages, ChatOptions options)
    {
        using var capture = new BodyCapturingHandler();
        using var httpClient = new HttpClient(capture);

        // Build the SAME Responses-backed IChatClient the factory builds, wrapped in the PRODUCTION store-disabling
        // decorator (the live send path's Codex boundary), pointed at the capturing transport. This proves the wrapper
        // itself emits the include + store=false + reasoning while keeping the tool/function items intact.
        var inner = CodexChatClientConstruction.Build(new Uri("https://chatgpt.com/backend-api/codex"),
            httpClient,
            "gpt-5.4");
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");

        try
        {
            await client.GetResponseAsync(messages, options);
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
#pragma warning restore OPENAI001
