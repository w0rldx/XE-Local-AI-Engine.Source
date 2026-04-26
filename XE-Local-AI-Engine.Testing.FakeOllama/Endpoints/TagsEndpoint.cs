namespace XE_Local_AI_Engine.Testing.FakeOllama;

using Microsoft.AspNetCore.Http;

internal static class TagsEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, FakeOllamaState state)
    {
        FakeOllamaEndpointMapper.Record(context, state, null, 0, null);

        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, null).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        var models = state.Models.Select(model => new
        {
            name = model,
            modified_at = FakeOllamaEndpointMapper.NowString(),
            size = 1,
            digest = "sha256:fake",
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
