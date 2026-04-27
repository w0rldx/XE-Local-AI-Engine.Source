namespace XE_Local_AI_Engine.Testing.FakeOllama;

using Microsoft.AspNetCore.Http;

internal static class PsEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, FakeOllamaState state)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);

        FakeOllamaEndpointMapper.Record(context, state, null, 0, null);
        if (await FakeOllamaEndpointMapper.TryApplyFailureAsync(context, state, null).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        return Results.Json(new
        {
            models = state.RunningModels.Select(model => new
            {
                name = model.Name,
                model = model.Name,
                expires_at = model.ExpiresAt,
                size_vram = 0,
                context_length = 4096
            })
        }, FakeOllamaEndpointMapper.SerializerOptions);
    }
}
