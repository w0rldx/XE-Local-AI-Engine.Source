namespace XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

using Microsoft.AspNetCore.Http;

internal static class TagsEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, FakeOllamaState state)
    {
        FakeOllamaEndpointMapper.Record(context, state, model: null, messageCount: 0, prompt: null);

        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, model: null).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        var models = state.Models.Select(model => new
        {
            name = model,
            modified_at = FakeOllamaEndpointMapper.NowString(),
            size = 1,
            digest = state.ModelDigests.TryGetValue(model, out var digest) ? digest : "sha256:fake",
            details = new
            {
                format = "gguf",
                family = "fake",
                families = new[]
                {
                    "fake"
                },
                parameter_size = "0B",
                quantization_level = "Q0_0"
            }
        });

        return Results.Json(new
        {
            models
        }, FakeOllamaEndpointMapper.SerializerOptions);
    }
}
