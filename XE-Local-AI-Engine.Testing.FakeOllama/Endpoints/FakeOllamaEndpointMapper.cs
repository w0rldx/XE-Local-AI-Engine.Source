namespace XE_Local_AI_Engine.Testing.FakeOllama.Endpoints;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Testing.FakeOllama.Determinism;

internal static class FakeOllamaEndpointMapper
{
    internal static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static void MapFakeOllamaEndpoints(this WebApplication app, FakeOllamaState state)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(state);

        app.MapMethods("/", [HttpMethods.Head], () => Results.Ok());
        app.MapGet("/", () => Results.Text("Ollama is running"));
        app.MapGet("/api/version", () => Results.Json(new
        {
            version = "0.0.0-fake"
        }, SerializerOptions));
        app.MapGet("/api/ps", (Delegate)((HttpContext context) => PsEndpoint.HandleAsync(context, state)));
        app.MapGet("/api/tags", (Delegate)((HttpContext context) => TagsEndpoint.HandleAsync(context, state)));
        app.MapPost("/api/show", (Delegate)((HttpContext context) => ShowEndpoint.HandleAsync(context, state)));
        app.MapPost("/api/pull", (Delegate)((HttpContext context) => PullEndpoint.HandleAsync(context, state)));
        app.MapDelete("/api/delete", (Delegate)((HttpContext context) => DeleteEndpoint.HandleAsync(context, state)));
        app.MapPost("/api/chat", (Delegate)((HttpContext context) => ChatEndpoint.HandleAsync(context, state)));
        app.MapPost("/api/generate", (Delegate)((HttpContext context) => GenerateEndpoint.HandleAsync(context, state)));
        app.MapPost("/api/embed", (Delegate)((HttpContext context) => EmbedEndpoint.HandleAsync(context, state, false)));
        app.MapPost("/api/embeddings", (Delegate)((HttpContext context) => EmbedEndpoint.HandleAsync(context, state, true)));
        app.MapPost("/test/failures", (Delegate)((HttpContext context) => TestControlEndpoints.EnqueueFailureAsync(context, state)));
        app.MapDelete("/test/failures", (HttpContext context) => TestControlEndpoints.ClearFailures(context, state));
        app.MapGet("/test/requests", (HttpContext context) => TestControlEndpoints.GetRequests(context, state));
        app.MapDelete("/test/requests", (HttpContext context) => TestControlEndpoints.ClearRequests(context, state));
        app.MapPost("/test/script", (Delegate)((HttpContext context) => TestControlEndpoints.SetScriptAsync(context, state)));
    }

    internal static async Task<JsonDocument?> ReadJsonAsync(HttpContext context)
    {
        if (context.Request.ContentLength is 0)
        {
            return null;
        }

        return await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    internal static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    internal static bool StreamEnabled(JsonElement element)
    {
        return !element.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.False;
    }

    internal static async Task<bool> TryApplyFailureAsync(HttpContext context, FakeOllamaState state, string? model)
    {
        if (!state.TryDequeueFailure(out var failure))
        {
            return false;
        }

        switch (failure)
        {
            case FakeOllamaFailure.ModelUnavailable:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = $"model '{model ?? "unknown"}' not found"
                }, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
                return true;
            case FakeOllamaFailure.Timeout:
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
                return true;
            case FakeOllamaFailure.MalformedJson:
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{not_json", context.RequestAborted).ConfigureAwait(false);
                return true;
            case FakeOllamaFailure.EmptyResponse:
                context.Response.StatusCode = StatusCodes.Status200OK;
                return true;
            case FakeOllamaFailure.Http500:
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "fake ollama injected failure"
                }, SerializerOptions, context.RequestAborted).ConfigureAwait(false);
                return true;
            case FakeOllamaFailure.PartialStream:
                await WriteNdjsonAsync(context, new[]
                {
                    new
                    {
                        model,
                        created_at = NowString(),
                        response = "partial",
                        done = false
                    },
                    new
                    {
                        model,
                        created_at = NowString(),
                        response = " stream",
                        done = false
                    }
                }).ConfigureAwait(false);
                return true;
            default:
                throw new InvalidOperationException($"Unsupported fake Ollama failure: {failure}.");
        }
    }

    internal static async Task WriteNdjsonAsync(HttpContext context, IEnumerable<object> items)
    {
        context.Response.ContentType = "application/x-ndjson";
        foreach (var item in items)
        {
            var line = JsonSerializer.Serialize(item, SerializerOptions);
            await context.Response.WriteAsync(line, context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync("\n", context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

    internal static void Record(HttpContext context, FakeOllamaState state, string? model, int messageCount, string? prompt)
    {
        state.Record(new FakeOllamaRequest(context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            model,
            messageCount,
            prompt is null ? null : ComputePromptHash(prompt),
            DateTimeOffset.UtcNow));
    }

    internal static IReadOnlyList<double> Embed(string input, int dimensions)
    {
        return EmbeddingDeterminism.EmbedDeterministic(input, dimensions);
    }

    internal static string NowString()
    {
        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static bool IsAuthorized(HttpContext context, FakeOllamaState state)
    {
        if (string.IsNullOrWhiteSpace(state.ControlEndpointToken))
        {
            return true;
        }

        return context.Request.Headers.TryGetValue("X-Test-Sink-Token", out var header)
               && string.Equals(header.ToString(), state.ControlEndpointToken, StringComparison.Ordinal);
    }

    private static string ComputePromptHash(string prompt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        return Convert.ToHexString(hash);
    }
}
