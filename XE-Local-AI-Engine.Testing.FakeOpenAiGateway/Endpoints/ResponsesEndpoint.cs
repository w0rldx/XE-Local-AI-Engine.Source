namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Endpoints;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>POST responses</c>: every answer leads with a reasoning item carrying
///     <see cref="FakeOpenAiGatewayServer.EncryptedReasoning" />, then the scripted function call or the reply text, so a
///     test can prove a stateless tool loop replays the reasoning.
/// </summary>
internal static class ResponsesEndpoint
{
    private const string ResponseId = "resp_fake";
    private const string ReasoningId = "rs_fake_0";
    private const string MessageId = "msg_fake_0";
    private const string FunctionCallId = "fc_fake_0";
    private const string CallId = "call_fake_0";

    public static async Task<IResult> HandleAsync(HttpContext context, FakeOpenAiGatewayState state)
    {
        var raw = await FakeOpenAiGatewayEndpointMapper.ReadBodyAsync(context);
        JsonDocument body;
        try
        {
            body = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            if (await FakeOpenAiGatewayEndpointMapper.TryRejectAsync(context, state, model: null, stream: false, messageCount: 0, toolCount: 0, raw))
            {
                return Results.Empty;
            }

            await FakeOpenAiGatewayEndpointMapper.WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "The request body is not valid JSON.", "invalid_request_error", code: null);
            return Results.Empty;
        }

        using (body)
        {
            var root = body.RootElement;
            var model = FakeOpenAiGatewayEndpointMapper.GetString(root, "model");
            var stream = root.TryGetProperty("stream", out var streamFlag) && streamFlag.ValueKind == JsonValueKind.True;
            var inputCount = root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array ? input.GetArrayLength() : 1;

            if (await FakeOpenAiGatewayEndpointMapper.TryRejectAsync(context, state, model, stream, inputCount, FakeOpenAiGatewayEndpointMapper.ArrayLength(root, "tools"), raw))
            {
                return Results.Empty;
            }

            if (!state.Options.Models.Any(known => string.Equals(known.Id, model, StringComparison.Ordinal)))
            {
                await FakeOpenAiGatewayEndpointMapper.WriteUnknownModelAsync(context, model);
                return Results.Empty;
            }

            // The real gateway stores a response unless told otherwise, so an absent flag echoes true.
            var store = !root.TryGetProperty("store", out var storeFlag) || storeFlag.ValueKind != JsonValueKind.False;
            var script = state.TakeScript();
            var text = script?.CompletionText ?? state.Options.DefaultCompletionText;
            var output = new JsonArray(Reasoning(), script?.ToolCall is { } toolCall ? FunctionCall(toolCall, toolCall.Arguments) : Message(text));
            var usage = Usage(inputCount, script?.ToolCall is not null ? 1 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

            if (stream)
            {
                await StreamAsync(context, model, store, script, text, output, usage);
            }
            else
            {
                await FakeOpenAiGatewayEndpointMapper.WriteJsonAsync(context, Response(model, store, "completed", output, usage));
            }

            return Results.Empty;
        }
    }

    private static async Task StreamAsync(HttpContext context,
        string? model,
        bool store,
        FakeOpenAiGatewayScript? script,
        string text,
        JsonArray output,
        JsonObject usage)
    {
        FakeOpenAiGatewayEndpointMapper.StartEventStream(context);
        var sequence = 0;

        async Task Emit(string type, JsonObject fields)
        {
            fields["type"] = type;
            fields["sequence_number"] = ++sequence;
            await context.Response.WriteAsync($"event: {type}\n", context.RequestAborted);
            await FakeOpenAiGatewayEndpointMapper.WriteEventAsync(context, fields.ToJsonString());
        }

        await Emit("response.created", new JsonObject
        {
            ["response"] = Response(model, store, "in_progress", [], usage: null)
        });

        await Emit("response.output_item.added", Item(0, Reasoning()));
        await Emit("response.output_item.done", Item(0, Reasoning()));

        if (script?.ToolCall is { } toolCall)
        {
            // The arguments arrive in two pieces, so a client must take the whole call from the done event.
            var half = toolCall.Arguments.Length / 2;
            await Emit("response.output_item.added", Item(1, FunctionCall(toolCall, arguments: string.Empty)));
            foreach (var piece in new[] { toolCall.Arguments[..half], toolCall.Arguments[half..] })
            {
                await Emit("response.function_call_arguments.delta", ItemEvent(FunctionCallId, piece, "delta"));
            }

            await Emit("response.function_call_arguments.done", ItemEvent(FunctionCallId, toolCall.Arguments, "arguments"));
            await Emit("response.output_item.done", Item(1, FunctionCall(toolCall, toolCall.Arguments)));
        }
        else
        {
            var emptyMessage = Message(text);
            emptyMessage["status"] = "in_progress";
            emptyMessage["content"] = new JsonArray();
            await Emit("response.output_item.added", Item(1, emptyMessage));
            await Emit("response.content_part.added", PartEvent(OutputText(string.Empty)));
            foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((word, index) => index == 0 ? word : " " + word))
            {
                var delta = PartEvent(part: null);
                delta["delta"] = token;
                await Emit("response.output_text.delta", delta);
            }

            var done = PartEvent(part: null);
            done["text"] = text;
            await Emit("response.output_text.done", done);
            await Emit("response.content_part.done", PartEvent(OutputText(text)));
            await Emit("response.output_item.done", Item(1, Message(text)));
        }

        await Emit("response.completed", new JsonObject
        {
            ["response"] = Response(model, store, "completed", output, usage)
        });
    }

    private static JsonObject Response(string? model, bool store, string status, JsonArray output, JsonObject? usage)
    {
        return new JsonObject
        {
            ["id"] = ResponseId,
            ["object"] = "response",
            ["created_at"] = 0,
            ["status"] = status,
            ["model"] = model,
            ["store"] = store,
            ["output"] = output.DeepClone(),
            ["usage"] = usage?.DeepClone()
        };
    }

    private static JsonObject Reasoning()
    {
        return new JsonObject
        {
            ["id"] = ReasoningId,
            ["type"] = "reasoning",
            ["summary"] = new JsonArray(),
            ["encrypted_content"] = FakeOpenAiGatewayServer.EncryptedReasoning
        };
    }

    private static JsonObject FunctionCall(FakeOpenAiGatewayToolCall toolCall, string arguments)
    {
        return new JsonObject
        {
            ["id"] = FunctionCallId,
            ["type"] = "function_call",
            ["status"] = "completed",
            ["call_id"] = CallId,
            ["name"] = toolCall.Name,
            ["arguments"] = arguments
        };
    }

    private static JsonObject Message(string text)
    {
        return new JsonObject
        {
            ["id"] = MessageId,
            ["type"] = "message",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray(OutputText(text))
        };
    }

    private static JsonObject OutputText(string text)
    {
        return new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = text,
            ["annotations"] = new JsonArray()
        };
    }

    private static JsonObject Item(int outputIndex, JsonObject item)
    {
        return new JsonObject
        {
            ["output_index"] = outputIndex,
            ["item"] = item
        };
    }

    private static JsonObject ItemEvent(string itemId, string value, string field)
    {
        return new JsonObject
        {
            ["output_index"] = 1,
            ["item_id"] = itemId,
            [field] = value
        };
    }

    private static JsonObject PartEvent(JsonObject? part)
    {
        var fields = new JsonObject
        {
            ["output_index"] = 1,
            ["item_id"] = MessageId,
            ["content_index"] = 0
        };
        if (part is not null)
        {
            fields["part"] = part;
        }

        return fields;
    }

    private static JsonObject Usage(int inputTokens, int outputTokens)
    {
        return new JsonObject
        {
            ["input_tokens"] = inputTokens,
            ["input_tokens_details"] = new JsonObject
            {
                ["cached_tokens"] = 0
            },
            ["output_tokens"] = outputTokens,
            ["output_tokens_details"] = new JsonObject
            {
                ["reasoning_tokens"] = 0
            },
            ["total_tokens"] = inputTokens + outputTokens
        };
    }
}
