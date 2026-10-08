namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Endpoints;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

internal static class ChatCompletionsEndpoint
{
    private const string CompletionId = "chatcmpl-fake";
    private const string ToolCallId = "call_fake_0";

    public static async Task<IResult> HandleAsync(HttpContext context, FakeOpenAiGatewayState state)
    {
        JsonDocument body;
        try
        {
            body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            if (await FakeOpenAiGatewayEndpointMapper.TryRejectAsync(context, state, model: null, stream: false, messageCount: 0, toolCount: 0))
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
            var messageCount = FakeOpenAiGatewayEndpointMapper.ArrayLength(root, "messages");

            if (await FakeOpenAiGatewayEndpointMapper.TryRejectAsync(context, state, model, stream, messageCount, FakeOpenAiGatewayEndpointMapper.ArrayLength(root, "tools")))
            {
                return Results.Empty;
            }

            if (!state.Options.Models.Any(known => string.Equals(known.Id, model, StringComparison.Ordinal)))
            {
                await FakeOpenAiGatewayEndpointMapper.WriteUnknownModelAsync(context, model);
                return Results.Empty;
            }

            var script = state.TakeScript();
            var usage = Usage(PromptTokens(root), script?.ToolCall is not null ? 1 : SplitTokens(Text(state, script)).Count);
            if (stream)
            {
                await StreamAsync(context, model, state, script, usage);
            }
            else
            {
                await FakeOpenAiGatewayEndpointMapper.WriteJsonAsync(context, Completion(model, state, script, usage));
            }

            return Results.Empty;
        }
    }

    internal static JsonObject Chunk(string? model, JsonObject delta, string? finishReason)
    {
        return new JsonObject
        {
            ["id"] = CompletionId,
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finishReason
            })
        };
    }

    private static JsonObject Completion(string? model, FakeOpenAiGatewayState state, FakeOpenAiGatewayScript? script, JsonObject usage)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant"
        };
        string finishReason;
        if (script?.ToolCall is { } toolCall)
        {
            message["content"] = null;
            message["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = ToolCallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolCall.Name,
                    ["arguments"] = toolCall.Arguments
                }
            });
            finishReason = "tool_calls";
        }
        else
        {
            message["content"] = Text(state, script);
            finishReason = "stop";
        }

        return new JsonObject
        {
            ["id"] = CompletionId,
            ["object"] = "chat.completion",
            ["created"] = 0,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = finishReason
            }),
            ["usage"] = usage
        };
    }

    private static async Task StreamAsync(HttpContext context, string? model, FakeOpenAiGatewayState state, FakeOpenAiGatewayScript? script, JsonObject usage)
    {
        FakeOpenAiGatewayEndpointMapper.StartEventStream(context);
        await WriteChunkAsync(context, Chunk(model, new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = string.Empty
        }, finishReason: null));

        string finishReason;
        if (script?.ToolCall is { } toolCall)
        {
            // Name first, then the arguments in two pieces, so a client must accumulate them by index.
            var half = toolCall.Arguments.Length / 2;
            await WriteChunkAsync(context, Chunk(model, ToolCallDelta(new JsonObject
            {
                ["index"] = 0,
                ["id"] = ToolCallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolCall.Name,
                    ["arguments"] = string.Empty
                }
            }), finishReason: null));
            await WriteChunkAsync(context, Chunk(model, ToolCallDelta(ArgumentsDelta(toolCall.Arguments[..half])), finishReason: null));
            await WriteChunkAsync(context, Chunk(model, ToolCallDelta(ArgumentsDelta(toolCall.Arguments[half..])), finishReason: null));
            finishReason = "tool_calls";
        }
        else
        {
            foreach (var token in SplitTokens(Text(state, script)))
            {
                await WriteChunkAsync(context, Chunk(model, new JsonObject
                {
                    ["content"] = token
                }, finishReason: null));
            }

            finishReason = "stop";
        }

        await WriteChunkAsync(context, Chunk(model, new JsonObject(), finishReason));

        var usageChunk = Chunk(model, new JsonObject(), finishReason: null);
        usageChunk["choices"] = new JsonArray();
        usageChunk["usage"] = usage;
        await WriteChunkAsync(context, usageChunk);
        await FakeOpenAiGatewayEndpointMapper.WriteEventAsync(context, "[DONE]");
    }

    private static Task WriteChunkAsync(HttpContext context, JsonObject chunk)
    {
        return FakeOpenAiGatewayEndpointMapper.WriteEventAsync(context, chunk.ToJsonString());
    }

    private static JsonObject ToolCallDelta(JsonObject toolCall)
    {
        return new JsonObject
        {
            ["tool_calls"] = new JsonArray(toolCall)
        };
    }

    private static JsonObject ArgumentsDelta(string arguments)
    {
        return new JsonObject
        {
            ["index"] = 0,
            ["function"] = new JsonObject
            {
                ["arguments"] = arguments
            }
        };
    }

    private static JsonObject Usage(int promptTokens, int completionTokens)
    {
        return new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = promptTokens + completionTokens
        };
    }

    /// <summary>A deterministic stand-in for a tokenizer: one token per character of string message content.</summary>
    private static int PromptTokens(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return messages.EnumerateArray().Sum(message => FakeOpenAiGatewayEndpointMapper.GetString(message, "content")?.Length ?? 0);
    }

    private static string Text(FakeOpenAiGatewayState state, FakeOpenAiGatewayScript? script)
    {
        return script?.CompletionText ?? state.Options.DefaultCompletionText;
    }

    private static List<string> SplitTokens(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Select((word, index) => index == 0 ? word : " " + word)
                   .ToList();
    }
}
