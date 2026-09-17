namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.Net.Mime;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

/// <summary>
///     Requires a per-instance bearer token on every bridge route, and stashes the caller it identifies for the
///     routes behind it.
///     <para>
///         It runs after the same-host peer guard, not instead of it. The guard answers "is this machine allowed to
///         talk to the bridge at all"; this answers "which installed application is talking". Neither substitutes for
///         the other: every container on an engine-owned network passes the guard, so without this any one of them
///         could use another's bridge.
///     </para>
///     <para>
///         A plain middleware rather than an ASP.NET authentication scheme. A scheme buys principal construction,
///         claims transformation and policy evaluation, none of which this surface has any use for — the whole
///         decision is one token against one row.
///     </para>
/// </summary>
public sealed class ContainerBridgeTokenMiddleware : IMiddleware
{
    /// <summary>The OpenAI error envelope, so an OpenAI-compatible client inside a container surfaces the reason instead of an empty failure.</summary>
    internal const string UnauthorizedBody =
        """{"error":{"message":"The container bridge requires the bearer token the engine issued to this application.","type":"invalid_request_error","code":"invalid_api_key"}}""";

    private const string BearerPrefix = "Bearer ";

    private readonly ILogger<ContainerBridgeTokenMiddleware> _logger;
    private readonly IContainerBridgeTokenVerifier _verifier;

    public ContainerBridgeTokenMiddleware(IContainerBridgeTokenVerifier verifier, ILogger<ContainerBridgeTokenMiddleware> logger)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = ReadBearerToken(context);
        if (presented is null)
        {
            await RefuseAsync(context, "no bearer token was presented");
            return;
        }

        var caller = await _verifier.VerifyAsync(presented, context.RequestAborted);
        if (caller is null)
        {
            await RefuseAsync(context, "the presented token did not verify");
            return;
        }

        // A feature rather than Items: the routes behind this read it by type, and a typed slot cannot be shadowed by
        // some other component choosing the same string key.
        context.Features.Set(caller);
        await next(context);
    }

    private static string? ReadBearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[BearerPrefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private async Task RefuseAsync(HttpContext context, string reason)
    {
        // The reason is logged, never returned: a caller learning WHICH half failed learns whether an instance id
        // exists, and this surface answers every failure identically.
        _logger.LogWarning("The container bridge refused a request to {Path}: {Reason}.", context.Request.Path, reason);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsync(UnauthorizedBody, context.RequestAborted);
    }
}
