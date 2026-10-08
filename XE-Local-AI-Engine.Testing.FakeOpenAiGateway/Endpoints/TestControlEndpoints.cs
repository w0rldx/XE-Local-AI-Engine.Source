namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

internal static class TestControlEndpoints
{
    public static async Task<IResult> EnqueueFailureAsync(HttpContext context, FakeOpenAiGatewayState state)
    {
        if (!FakeOpenAiGatewayEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        var request = await ReadAsync(context, FakeOpenAiGatewayJsonContext.Default.FakeOpenAiGatewayFailureRequest);
        if (request is null || !Enum.TryParse<FakeOpenAiGatewayFailure>(request.Failure, ignoreCase: true, out var failure)
                            || !Enum.IsDefined(failure))
        {
            return Results.BadRequest("A valid failure name is required.");
        }

        state.EnqueueFailure(failure);
        return Results.Accepted();
    }

    public static IResult ClearFailures(HttpContext context, FakeOpenAiGatewayState state)
    {
        if (!FakeOpenAiGatewayEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        state.ClearFailures();
        return Results.NoContent();
    }

    public static IResult GetRequests(HttpContext context, FakeOpenAiGatewayState state)
    {
        if (!FakeOpenAiGatewayEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        return Results.Json(state.RecordedRequests.ToArray(), FakeOpenAiGatewayJsonContext.Default.FakeOpenAiGatewayRequestArray);
    }

    public static IResult ClearRequests(HttpContext context, FakeOpenAiGatewayState state)
    {
        if (!FakeOpenAiGatewayEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        state.ClearRequests();
        return Results.NoContent();
    }

    public static async Task<IResult> SetScriptAsync(HttpContext context, FakeOpenAiGatewayState state)
    {
        if (!FakeOpenAiGatewayEndpointMapper.IsAuthorized(context, state))
        {
            return Results.Unauthorized();
        }

        var script = await ReadAsync(context, FakeOpenAiGatewayJsonContext.Default.FakeOpenAiGatewayScript);
        if (script is null || script.Count < 1 || (script.CompletionText is null && script.ToolCall is null))
        {
            return Results.BadRequest("A script needs completionText or toolCall and a count of at least 1.");
        }

        state.SetScript(script);
        return Results.NoContent();
    }

    private static async Task<T?> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync(typeInfo, context.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
