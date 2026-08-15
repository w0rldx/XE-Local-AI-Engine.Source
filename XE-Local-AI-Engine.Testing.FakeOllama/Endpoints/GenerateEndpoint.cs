namespace XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

using Microsoft.AspNetCore.Http;

internal static class GenerateEndpoint
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
        var prompt = FakeOllamaEndpointMapper.GetString(root, "prompt") ?? string.Empty;
        var response = $"[fake-ollama] {prompt}";
        var tokens = SplitTokens(response);

        var keepAlive = FakeOllamaEndpointMapper.GetKeepAlive(root);
        FakeOllamaEndpointMapper.Record(context, state, model, messageCount: 0, prompt, keepAlive);

        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, model).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        // keep_alive=0 is Ollama's eviction request — it is how the eject action unloads a model — and real Ollama drops
        // the model from /api/ps immediately. Mirror that here so an eject is observable in the running-models list —
        // but only after the injected-failure gate above, so a simulated timeout/404/500 leaves the model loaded, as it
        // would be on real Ollama.
        if (keepAlive is "0")
        {
            state.RunningModels = state.RunningModels
                                       .Where(running => !string.Equals(running.Name, model, StringComparison.OrdinalIgnoreCase))
                                       .ToArray();
        }

        if (!FakeOllamaEndpointMapper.StreamEnabled(root))
        {
            return Results.Json(new
            {
                model,
                created_at = FakeOllamaEndpointMapper.NowString(),
                response,
                done = true,
                done_reason = "stop",
                context = Array.Empty<int>(),
                total_duration = 1,
                load_duration = 1,
                prompt_eval_count = prompt.Length,
                eval_count = tokens.Count,
                eval_duration = 1
            }, FakeOllamaEndpointMapper.SerializerOptions);
        }

        var chunks = tokens.Select(token => new
        {
            model,
            created_at = FakeOllamaEndpointMapper.NowString(),
            response = token,
            done = false
        }).Cast<object>().Append(new
        {
            model,
            created_at = FakeOllamaEndpointMapper.NowString(),
            response = string.Empty,
            done = true,
            done_reason = "stop",
            context = Array.Empty<int>(),
            total_duration = 1,
            load_duration = 1,
            prompt_eval_count = prompt.Length,
            eval_count = tokens.Count,
            eval_duration = 1
        });

        await FakeOllamaEndpointMapper.WriteNdjsonAsync(context, chunks).ConfigureAwait(false);
        return Results.Empty;
    }

    private static IReadOnlyList<string> SplitTokens(string value)
    {
        return value.Split(separator: ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select((token, index) => index == 0 ? token : " " + token)
                    .DefaultIfEmpty(string.Empty)
                    .ToArray();
    }
}
