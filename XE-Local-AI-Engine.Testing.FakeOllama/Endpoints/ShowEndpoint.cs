namespace XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

using Microsoft.AspNetCore.Http;

internal static class ShowEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, FakeOllamaState state)
    {
        using var body = await FakeOllamaEndpointMapper.ReadJsonAsync(context).ConfigureAwait(false);
        var root = body?.RootElement;
        var model = root is null ? null : FakeOllamaEndpointMapper.GetString(root.Value, "model");

        FakeOllamaEndpointMapper.Record(context, state, model, messageCount: 0, model);

        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, model).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        var modelInfo = model is not null && state.ModelInfo.TryGetValue(model, out var configuredModelInfo)
            ? configuredModelInfo
            : new Dictionary<string, object?>();

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
            model_info = modelInfo,
            capabilities = new[]
            {
                "completion",
                "embedding",
                // The default fake chat model advertises Ollama `tools` so capability-gated UI (the
                // chat local-tools toggle) renders, mirroring a real tool-capable model (e.g. qwen).
                "tools"
            },
            modified_at = FakeOllamaEndpointMapper.NowString()
        }, FakeOllamaEndpointMapper.SerializerOptions);
    }
}
