namespace XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

using Microsoft.AspNetCore.Http;

internal static class TestControlEndpoints
{
    public static async Task<IResult> EnqueueFailureAsync(HttpContext context, FakeOllamaState state)
    {
        if (!FakeOllamaEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        var request = await context.Request.ReadFromJsonAsync(FakeOllamaJsonContext.Default.FakeOllamaFailureRequest, context.RequestAborted).ConfigureAwait(false);
        if (request is null || !Enum.TryParse<FakeOllamaFailure>(request.Failure, true, out var failure))
        {
            return Results.BadRequest(new
            {
                error = "Valid failure is required."
            });
        }

        state.EnqueueFailure(failure);
        return Results.Accepted();
    }

    public static IResult ClearFailures(HttpContext context, FakeOllamaState state)
    {
        if (!FakeOllamaEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        state.ClearFailures();
        return Results.NoContent();
    }

    public static IResult GetRequests(HttpContext context, FakeOllamaState state)
    {
        if (!FakeOllamaEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        return Results.Json(state.RecordedRequests, FakeOllamaEndpointMapper.SerializerOptions);
    }

    public static IResult ClearRequests(HttpContext context, FakeOllamaState state)
    {
        if (!FakeOllamaEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        state.ClearRequests();
        return Results.NoContent();
    }

    public static async Task<IResult> SetScriptAsync(HttpContext context, FakeOllamaState state)
    {
        if (!FakeOllamaEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        var request = await context.Request.ReadFromJsonAsync(FakeOllamaJsonContext.Default.FakeOllamaScriptRequest, context.RequestAborted).ConfigureAwait(false);
        if (request is null)
        {
            return Results.BadRequest(new
            {
                error = "Script tokens are required."
            });
        }

        state.ChatScript = _ => ToAsyncEnumerable(request.Tokens);
        return Results.NoContent();
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            yield return token;
            await Task.Yield();
        }
    }
}
