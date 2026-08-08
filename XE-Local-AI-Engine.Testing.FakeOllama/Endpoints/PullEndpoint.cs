namespace XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

using Microsoft.AspNetCore.Http;

internal static class PullEndpoint
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

        if (!state.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            state.Models = state.Models.Append(model).ToArray();
        }

        await FakeOllamaEndpointMapper.WriteNdjsonAsync(context, new object[]
        {
            new
            {
                status = "pulling manifest"
            },
            new
            {
                status = "pulling layers",
                digest = "sha256:fake-layer",
                total = 100L,
                completed = 100L
            },
            new
            {
                status = "success"
            }
        }).ConfigureAwait(false);

        return Results.Empty;
    }
}
