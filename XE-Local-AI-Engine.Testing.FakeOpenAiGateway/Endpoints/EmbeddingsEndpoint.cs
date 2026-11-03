namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Endpoints;

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Determinism;

/// <summary><c>POST embeddings</c>: one deterministic vector per input, in the <c>encoding_format</c> the caller asked for.</summary>
internal static class EmbeddingsEndpoint
{
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
            if (await FakeOpenAiGatewayEndpointMapper.TryRejectAsync(context, state, model, stream: false, messageCount: 0, toolCount: 0, raw))
            {
                return Results.Empty;
            }

            if (!state.Serves(model))
            {
                await FakeOpenAiGatewayEndpointMapper.WriteUnknownModelAsync(context, model);
                return Results.Empty;
            }

            var inputs = ReadInputs(root);
            var dimensions = root.TryGetProperty("dimensions", out var requested) && requested.TryGetInt32(out var value) && value > 0
                ? value
                : state.Options.EmbeddingDimensions;
            var base64 = string.Equals(FakeOpenAiGatewayEndpointMapper.GetString(root, "encoding_format"), "base64", StringComparison.Ordinal);

            var data = new JsonArray();
            for (var index = 0; index < inputs.Count; index++)
            {
                var vector = EmbeddingDeterminism.EmbedDeterministic(inputs[index], dimensions);
                data.Add(new JsonObject
                {
                    ["object"] = "embedding",
                    ["index"] = index,
                    ["embedding"] = base64 ? Base64Floats(vector) : new JsonArray(vector.Select(static component => (JsonNode?)JsonValue.Create(component)).ToArray())
                });
            }

            var promptTokens = inputs.Sum(static input => input.Length);
            await FakeOpenAiGatewayEndpointMapper.WriteJsonAsync(context, new JsonObject
            {
                ["object"] = "list",
                ["model"] = model,
                ["data"] = data,
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = promptTokens,
                    ["total_tokens"] = promptTokens
                }
            });
            return Results.Empty;
        }
    }

    private static List<string> ReadInputs(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input))
        {
            return [string.Empty];
        }

        return input.ValueKind == JsonValueKind.Array
            ? input.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToList()
            : [input.GetString() ?? string.Empty];
    }

    private static string Base64Floats(IReadOnlyList<double> vector)
    {
        var bytes = new byte[vector.Count * sizeof(float)];
        for (var i = 0; i < vector.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), (float)vector[i]);
        }

        return Convert.ToBase64String(bytes);
    }
}
