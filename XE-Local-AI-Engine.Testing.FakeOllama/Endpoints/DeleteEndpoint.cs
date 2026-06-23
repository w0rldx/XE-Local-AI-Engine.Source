namespace XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

using Microsoft.AspNetCore.Http;

internal static class DeleteEndpoint
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

        if (string.IsNullOrWhiteSpace(model))
        {
            return Results.BadRequest(new
            {
                error = "model is required"
            });
        }

        state.Models = state.Models
                            .Where(existing => !string.Equals(existing, model, StringComparison.OrdinalIgnoreCase))
                            .ToArray();

        state.ModelDigests.Remove(model);
        state.ModelInfo.Remove(model);

        return Results.Ok();
    }
}
