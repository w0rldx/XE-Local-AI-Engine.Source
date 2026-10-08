namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway.Endpoints;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static class FakeOpenAiGatewayEndpointMapper
{
    public static void MapFakeOpenAiGatewayEndpoints(this WebApplication app, FakeOpenAiGatewayState state)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(state);

        var api = app.MapGroup("/" + state.Options.BasePath.Trim('/'));
        api.MapGet("models", (Delegate)((HttpContext context) => ModelsEndpoint.HandleAsync(context, state)));
        api.MapPost("chat/completions", (Delegate)((HttpContext context) => ChatCompletionsEndpoint.HandleAsync(context, state)));

        app.MapPost("/test/failures", (Delegate)((HttpContext context) => TestControlEndpoints.EnqueueFailureAsync(context, state)));
        app.MapDelete("/test/failures", (HttpContext context) => TestControlEndpoints.ClearFailures(context, state));
        app.MapGet("/test/requests", (HttpContext context) => TestControlEndpoints.GetRequests(context, state));
        app.MapDelete("/test/requests", (HttpContext context) => TestControlEndpoints.ClearRequests(context, state));
        app.MapPost("/test/script", (Delegate)((HttpContext context) => TestControlEndpoints.SetScriptAsync(context, state)));
    }

    /// <summary>Record the request, then run the bearer, header and fault gates in that order.</summary>
    /// <returns>True when a gate already answered and the endpoint must stop.</returns>
    internal static async Task<bool> TryRejectAsync(HttpContext context, FakeOpenAiGatewayState state, string? model, bool stream, int messageCount, int toolCount)
    {
        state.Record(new FakeOpenAiGatewayRequest
        {
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            Model = model,
            Stream = stream,
            MessageCount = messageCount,
            ToolCount = toolCount,
            Headers = context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase)
        });

        var options = state.Options;
        if (options.RequiredBearerToken is not null
            && !string.Equals(context.Request.Headers.Authorization.ToString(), "Bearer " + options.RequiredBearerToken, StringComparison.Ordinal))
        {
            await WriteUnauthorizedAsync(context);
            return true;
        }

        var missing = options.RequiredHeaders.Keys.FirstOrDefault(name =>
            !string.Equals(context.Request.Headers[name].ToString(), options.RequiredHeaders[name], StringComparison.Ordinal));
        if (missing is not null)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                $"Missing or invalid required header '{missing}'.", "permission_error", "missing_required_header");
            return true;
        }

        return await TryApplyFailureAsync(context, state, model);
    }

    internal static Task WriteErrorAsync(HttpContext context, int statusCode, string message, string type, string? code)
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = type,
                ["param"] = null,
                ["code"] = code
            }
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(body.ToJsonString(), context.RequestAborted);
    }

    internal static Task WriteUnknownModelAsync(HttpContext context, string? model)
    {
        return WriteErrorAsync(context, StatusCodes.Status404NotFound,
            $"The model '{model}' does not exist or you do not have access to it.", "invalid_request_error", "model_not_found");
    }

    internal static Task WriteJsonAsync(HttpContext context, JsonNode body)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(body.ToJsonString(), context.RequestAborted);
    }

    internal static void StartEventStream(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
    }

    internal static async Task WriteEventAsync(HttpContext context, string data)
    {
        await context.Response.WriteAsync("data: " + data + "\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    internal static bool IsAuthorized(HttpContext context, FakeOpenAiGatewayState state)
    {
        var token = state.Options.ControlEndpointToken;
        return string.IsNullOrWhiteSpace(token)
               || string.Equals(context.Request.Headers["X-Test-Sink-Token"].ToString(), token, StringComparison.Ordinal);
    }

    internal static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    internal static int ArrayLength(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.GetArrayLength() : 0;
    }

    private static Task WriteUnauthorizedAsync(HttpContext context)
    {
        return WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
            "Incorrect API key provided.", "invalid_request_error", "invalid_api_key");
    }

    private static async Task<bool> TryApplyFailureAsync(HttpContext context, FakeOpenAiGatewayState state, string? model)
    {
        if (!state.TryDequeueFailure(out var failure))
        {
            return false;
        }

        switch (failure)
        {
            case FakeOpenAiGatewayFailure.Unauthorized:
                await WriteUnauthorizedAsync(context);
                break;
            case FakeOpenAiGatewayFailure.Forbidden:
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                    "Fake gateway injected a forbidden response.", "permission_error", "forbidden");
                break;
            case FakeOpenAiGatewayFailure.UnknownModel:
                await WriteUnknownModelAsync(context, model);
                break;
            case FakeOpenAiGatewayFailure.RateLimited:
                context.Response.Headers.RetryAfter = "7";
                await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests,
                    "Rate limit reached for requests. Please try again in 7s.", "requests", "rate_limit_exceeded");
                break;
            case FakeOpenAiGatewayFailure.Http500:
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                    "The server had an error while processing your request.", "server_error", code: null);
                break;
            case FakeOpenAiGatewayFailure.Hang:
                await HangAsync(context);
                break;
            case FakeOpenAiGatewayFailure.PartialStream:
                // An abort would reset the socket and drop the chunks unread, so close the connection after them instead.
                context.Response.Headers.Connection = "close";
                StartEventStream(context);
                await WriteEventAsync(context, ChatCompletionsEndpoint.Chunk(model, new JsonObject { ["role"] = "assistant", ["content"] = "partial" }, finishReason: null).ToJsonString());
                await WriteEventAsync(context, ChatCompletionsEndpoint.Chunk(model, new JsonObject { ["content"] = " stream" }, finishReason: null).ToJsonString());
                break;
            case FakeOpenAiGatewayFailure.MalformedJson:
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{not_json", context.RequestAborted);
                break;
            default:
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Unsupported fake gateway failure: {failure}."));
        }

        return true;
    }

    /// <summary>Never answer: wait until the caller gives up or the server stops, so disposal never waits on it.</summary>
    private static async Task HangAsync(HttpContext context)
    {
        var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }
        catch (OperationCanceledException)
        {
            context.Abort();
        }
    }
}
