namespace XE_Local_AI_Engine.Testing.FakeOllama;

using System.Text.Json;
using Microsoft.AspNetCore.Http;

internal static class EmbedEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, FakeOllamaState state, bool legacy)
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
        var model = FakeOllamaEndpointMapper.GetString(root, "model") ?? (state.Models.Count > 0 ? state.Models[^1] : "embeddings");
        var inputs = ReadInputs(root, legacy).ToArray();
        var firstInput = inputs.Length > 0 ? inputs[0] : string.Empty;
        var dimensions = ReadDimensions(root) ?? state.EmbeddingDimensions;

        FakeOllamaEndpointMapper.Record(context, state, model, 0, firstInput);

        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, model).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        var embeddings = (inputs.Length > 0 ? inputs : [string.Empty]).Select(input => FakeOllamaEndpointMapper.Embed(input, dimensions)).ToArray();
        return legacy
            ? Results.Json(new
            {
                embedding = embeddings[0]
            }, FakeOllamaEndpointMapper.SerializerOptions)
            : Results.Json(new
            {
                model,
                embeddings,
                total_duration = 1,
                load_duration = 1,
                prompt_eval_count = firstInput.Length
            }, FakeOllamaEndpointMapper.SerializerOptions);
    }

    private static IEnumerable<string> ReadInputs(JsonElement root, bool legacy)
    {
        if (legacy)
        {
            yield return FakeOllamaEndpointMapper.GetString(root, "prompt") ?? FakeOllamaEndpointMapper.GetString(root, "input") ?? string.Empty;
            yield break;
        }

        if (!root.TryGetProperty("input", out var input))
        {
            yield return string.Empty;
            yield break;
        }

        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                yield return item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText();
            }
        }
        else if (input.ValueKind == JsonValueKind.String)
        {
            yield return input.GetString() ?? string.Empty;
        }
        else
        {
            yield return input.GetRawText();
        }
    }

    private static int? ReadDimensions(JsonElement root)
    {
        return root.TryGetProperty("dimensions", out var dimensions) && dimensions.TryGetInt32(out var value) && value > 0 ? value : null;
    }
}
