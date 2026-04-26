namespace XE_Local_AI_Engine.Testing.FakeOllama;

using Microsoft.AspNetCore.Http;

internal static class ShowEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, FakeOllamaState state)
    {
        using var body = await FakeOllamaEndpointMapper.ReadJsonAsync(context).ConfigureAwait(false);
        var root = body?.RootElement;
        var model = root is null ? null : FakeOllamaEndpointMapper.GetString(root.Value, "model");

        FakeOllamaEndpointMapper.Record(context, state, model, 0, model);

        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, model).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        return Results.Json(new
        {
            license = "fake",
            modelfile = $"FROM {model ?? "fake"}",
            parameters = string.Empty,
            template = "{{ .Prompt }}",
            details = new
            {
                parent_model = string.Empty,
                format = "gguf",
                family = "fake",
                families = new[]
                {
                    "fake"
                },
                parameter_size = "0B",
                quantization_level = "Q0_0"
            },
            model_info = new Dictionary<string, object?>(),
            capabilities = new[]
            {
                "completion",
                "embedding"
            },
            modified_at = FakeOllamaEndpointMapper.NowString()
        }, FakeOllamaEndpointMapper.SerializerOptions);
    }
}
