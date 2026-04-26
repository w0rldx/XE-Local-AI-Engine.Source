namespace XE_Local_AI_Engine.Testing.FakeOllama;

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

        FakeOllamaEndpointMapper.Record(context, state, model, 0, prompt);

        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, model).ConfigureAwait(false))
        {
            return Results.Empty;
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
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select((token, index) => index == 0 ? token : " " + token)
                    .DefaultIfEmpty(string.Empty)
                    .ToArray();
    }
}
