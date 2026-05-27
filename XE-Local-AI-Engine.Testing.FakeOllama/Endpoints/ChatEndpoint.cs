namespace XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OllamaSharp.Models.Chat;

internal static class ChatEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, FakeOllamaState state)
    {
        using var body = await FakeOllamaEndpointMapper.ReadJsonAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            return Results.BadRequest(new
            {
                error = "request body is required"
            });
        }

        var root = body.RootElement;
        var model = FakeOllamaEndpointMapper.GetString(root, "model") ?? (state.Models.Count > 0 ? state.Models[0] : "chat");
        var messages = ReadMessages(root).ToArray();
        var lastUserMessage = messages.LastOrDefault(message => string.Equals(message.Role?.ToString(), "user", StringComparison.OrdinalIgnoreCase))?.Content
                              ?? (messages.Length > 0 ? messages[^1].Content : null)
                              ?? string.Empty;

        FakeOllamaEndpointMapper.Record(context, state, model, messages.Length, lastUserMessage);

        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, model).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        // Check for a scripted tool call before falling through to text tokens.
        if (state.ToolCallScript is not null)
        {
            var toolCall = state.ToolCallScript(messages);
            if (toolCall is not null)
            {
                await WriteToolCallChunksAsync(context, model, toolCall, lastUserMessage.Length).ConfigureAwait(false);
                return Results.Empty;
            }
        }

        var tokens = await ReadTokensAsync(state, model, messages, context.RequestAborted).ConfigureAwait(false);
        if (!FakeOllamaEndpointMapper.StreamEnabled(root))
        {
            return Results.Json(new
            {
                model,
                created_at = FakeOllamaEndpointMapper.NowString(),
                message = new
                {
                    role = "assistant",
                    content = string.Concat(tokens)
                },
                done = true,
                done_reason = "stop",
                total_duration = 1,
                load_duration = 1,
                prompt_eval_count = lastUserMessage.Length,
                eval_count = tokens.Count,
                eval_duration = 1
            }, FakeOllamaEndpointMapper.SerializerOptions);
        }

        var chunks = tokens.Select(token => new
        {
            model,
            created_at = FakeOllamaEndpointMapper.NowString(),
            message = new
            {
                role = "assistant",
                content = token
            },
            done = false
        }).Cast<object>().Append(new
        {
            model,
            created_at = FakeOllamaEndpointMapper.NowString(),
            message = new
            {
                role = "assistant",
                content = string.Empty
            },
            done = true,
            done_reason = "stop",
            total_duration = 1,
            load_duration = 1,
            prompt_eval_count = lastUserMessage.Length,
            eval_count = tokens.Count,
            eval_duration = 1
        });

        await FakeOllamaEndpointMapper.WriteNdjsonAsync(context, chunks).ConfigureAwait(false);
        return Results.Empty;
    }

    /// <summary>
    ///     Emits the Ollama streaming wire format for a single tool call followed by a done chunk.
    ///     OllamaSharp 5.x parses: <c>message.tool_calls[0].function.{name,arguments}</c>.
    /// </summary>
    private static async Task WriteToolCallChunksAsync(HttpContext context, string model, FakeOllamaToolCall toolCall, int promptEvalCount)
    {
        var argumentsJson = JsonSerializer.Serialize(toolCall.Arguments);

        // Tool-call chunk: content empty, tool_calls present, done=false.
        var toolCallChunk = new
        {
            model,
            created_at = FakeOllamaEndpointMapper.NowString(),
            message = new
            {
                role = "assistant",
                content = string.Empty,
                tool_calls = new[]
                {
                    new
                    {
                        function = new
                        {
                            name = toolCall.Name,
                            arguments = JsonSerializer.Deserialize<JsonElement>(argumentsJson)
                        }
                    }
                }
            },
            done = false
        };

        // Done chunk signals end of the tool-call turn.
        var doneChunk = new
        {
            model,
            created_at = FakeOllamaEndpointMapper.NowString(),
            message = new
            {
                role = "assistant",
                content = string.Empty
            },
            done = true,
            done_reason = "stop",
            total_duration = 1,
            load_duration = 1,
            prompt_eval_count = promptEvalCount,
            eval_count = 1,
            eval_duration = 1
        };

        await FakeOllamaEndpointMapper.WriteNdjsonAsync(context, new object[] { toolCallChunk, doneChunk }).ConfigureAwait(false);
    }

    private static IEnumerable<Message> ReadMessages(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var message in messages.EnumerateArray())
        {
            var role = FakeOllamaEndpointMapper.GetString(message, "role") ?? "user";
            var content = FakeOllamaEndpointMapper.GetString(message, "content") ?? string.Empty;
            yield return new Message(role, content);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadTokensAsync(FakeOllamaState state, string model, IReadOnlyList<Message> messages, CancellationToken ct)
    {
        if (state.ChatScript is not null)
        {
            var scripted = new List<string>();
            var request = new ChatRequest
            {
                Model = model,
                Messages = messages.ToList(),
                Stream = true
            };

            await foreach (var token in state.ChatScript(request).WithCancellation(ct).ConfigureAwait(false))
            {
                scripted.Add(token);
            }

            return scripted;
        }

        var lastUserMessage = messages.LastOrDefault(message => string.Equals(message.Role?.ToString(), "user", StringComparison.OrdinalIgnoreCase))?.Content
                              ?? (messages.Count > 0 ? messages[^1].Content : null)
                              ?? string.Empty;

        return SplitTokens($"[fake-ollama] {lastUserMessage}");
    }

    private static IReadOnlyList<string> SplitTokens(string value)
    {
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select((token, index) => index == 0 ? token : " " + token)
                    .DefaultIfEmpty(string.Empty)
                    .ToArray();
    }
}
