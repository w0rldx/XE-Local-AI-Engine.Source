namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

internal static class ModelsEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, FakeOpenAiGatewayState state)
    {
        if (await FakeOpenAiGatewayEndpointMapper.TryRejectAsync(context, state, model: null, stream: false, messageCount: 0, toolCount: 0))
        {
            return Results.Empty;
        }

        var data = new JsonArray();
        foreach (var model in state.Options.Models)
        {
            var entry = new JsonObject
            {
                ["id"] = model.Id,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = model.OwnedBy,
                ["context_length"] = model.ContextLength,
                ["max_output_tokens"] = model.MaxOutputTokens
            };

            if (model.Metadata is not null)
            {
                var metadata = new JsonObject();
                foreach (var pair in model.Metadata)
                {
                    metadata[pair.Key] = pair.Value;
                }

                entry["metadata"] = metadata;
            }

            data.Add(entry);
        }

        await FakeOpenAiGatewayEndpointMapper.WriteJsonAsync(context, new JsonObject { ["object"] = "list", ["data"] = data });
        return Results.Empty;
    }
}
