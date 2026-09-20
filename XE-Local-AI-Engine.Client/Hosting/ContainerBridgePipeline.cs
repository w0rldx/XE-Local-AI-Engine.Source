namespace XE_Local_AI_Engine.Client.Hosting;

using System.Net.Mime;
using Microsoft.Net.Http.Headers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Branches the bridge listener's connections into a pipeline of their own, first in the application pipeline.
/// </summary>
/// <remarks>
///     The branch is what makes the bridge a separate surface rather than a second door onto the existing one:
///     everything the node serves is registered AFTER it, so a request that arrived on the bridge port never reaches
///     any of it, and the predicate is the arrival port, so nothing mapped inside it is reachable on the main
///     listener. See docs/wiki/11-hosting-and-deployment.md ("The container bridge listener").
/// </remarks>
internal static class ContainerBridgePipeline
{
    /// <summary>
    ///     The bridge answers in the OpenAI-style error envelope its callers understand, not RFC 7807: on this
    ///     listener there is no SPA fallback to reach and an unmapped path is genuinely nothing.
    /// </summary>
    internal const string NotFoundBody =
        """{"error":{"message":"No such container bridge route.","type":"invalid_request_error","code":null}}""";

    internal const string MethodNotAllowedBody =
        """{"error":{"message":"That container bridge route does not accept this HTTP method.","type":"invalid_request_error","code":null}}""";

    /// <summary>
    ///     The OpenAI-compatible surface a container calls, deliberately OUTSIDE <c>/api/local/v1</c>.
    /// </summary>
    /// <remarks>
    ///     Inside that prefix <c>LocalApiSecurityMiddleware</c>'s loopback-peer check would reject exactly the traffic
    ///     the bridge exists to accept; the peer guard and the token gate above are what replace it.
    /// </remarks>
    internal const string ChatCompletionsPath = "/llm/v1/chat/completions";

    internal const string EmbeddingsPath = "/llm/v1/embeddings";

    internal const string ModelsPath = "/llm/v1/models";

    /// <summary>The configuration key <c>HostFilteringMiddleware</c> reads its allow list from.</summary>
    private const string AllowedHostsKey = "AllowedHosts";

    /// <summary>
    ///     Adds the bridge's own host names to <c>AllowedHosts</c>, without which the listener is unreachable.
    /// </summary>
    /// <remarks>
    ///     <c>HostFilteringMiddleware</c> is installed by an <c>IStartupFilter</c>, so it runs BEFORE every middleware
    ///     the composition root registers, the bridge branch included. Written through a dedicated in-memory provider
    ///     rather than the configuration indexer, because one highest-precedence provider cannot be replaced by a
    ///     reload. Why widening is safe, and what the indexer would have cost:
    ///     docs/wiki/11-hosting-and-deployment.md ("The container bridge listener").
    /// </remarks>
    internal static void AllowBridgeHost(WebApplicationBuilder builder, ResolvedContainerBridgeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);

        var allowed = new List<string>();
        var configured = builder.Configuration[AllowedHostsKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            allowed.AddRange(configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // Both spellings a container may present: the bound address a Linux container dials, and the alias a Docker
        // Desktop container dials, which arrives in the Host header as the name rather than as an address.
        allowed.AddRange(BridgeHostNames(endpoint).Where(host => !allowed.Contains(host, StringComparer.OrdinalIgnoreCase)));

        builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(AllowedHostsKey, string.Join(';', allowed))]);
    }

    /// <summary>The host names a container can legitimately put in the Host header when calling this bridge.</summary>
    internal static IReadOnlyList<string> BridgeHostNames(ResolvedContainerBridgeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var address = endpoint.BindAddress.ToString();
        var separator = endpoint.ContainerFacingEndpoint.LastIndexOf(':');
        var containerFacingHost = separator > 0 ? endpoint.ContainerFacingEndpoint[..separator] : endpoint.ContainerFacingEndpoint;

        return string.Equals(address, containerFacingHost, StringComparison.OrdinalIgnoreCase)
            ? [address]
            : [address, containerFacingHost];
    }

    /// <summary>
    ///     Maps the bridge branch. Called only when the bridge listener was actually opened, so
    ///     <paramref name="endpoint" /> always names an address and port Kestrel is bound to.
    /// </summary>
    internal static void Map(WebApplication app, ResolvedContainerBridgeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(endpoint);

        app.MapWhen(context => IsBridgeConnection(context, endpoint),
            bridge =>
            {
                // Before anything else on this listener: a peer that is not this computer is refused without ever
                // presenting a credential, so a wrong token and a wrong machine are never the same answer.
                bridge.UseMiddleware<ContainerBridgePeerGuardMiddleware>();

                // Then the per-instance token, on EVERY bridge route. The peer guard admits any container on an
                // engine-owned network; this is what stops one of them using another's bridge.
                bridge.UseMiddleware<ContainerBridgeTokenMiddleware>();

                // The same forwarder the loopback model proxy uses, not a second copy: the five-step sequence it owns —
                // model existence, supervisor, endpoint, inference lease, streamed forward, idle watchdog — is why this is safe.
                bridge.Run(HandleAsync);
            });
    }

    /// <summary>
    ///     Whether a connection arrived on the bridge listener.
    /// </summary>
    /// <remarks>
    ///     The discriminator is the socket's whole local end — address AND port — because it is the one fact a caller
    ///     cannot forge, and because the port alone is ambiguous on a node whose loopback listener happens to carry
    ///     the bridge's port.
    /// </remarks>
    internal static bool IsBridgeConnection(HttpContext context, ResolvedContainerBridgeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.Matches(context.Connection.LocalIpAddress, context.Connection.LocalPort);
    }

    /// <summary>
    ///     The bridge's three routes, matched by path and method directly rather than through a routing branch: the
    ///     surface is three paths and a terminal, and a branch-local router would be more moving parts than the
    ///     thing it routes.
    /// </summary>
    private static Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var forwarder = context.RequestServices.GetRequiredService<LocalModelProxyForwarder>();

        if (path.Equals(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsPost(context.Request.Method)
                ? forwarder.ForwardChatCompletionsAsync(context)
                : WriteMethodNotAllowedAsync(context, HttpMethods.Post);
        }

        if (path.Equals(EmbeddingsPath, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsPost(context.Request.Method)
                ? forwarder.ForwardEmbeddingsAsync(context)
                : WriteMethodNotAllowedAsync(context, HttpMethods.Post);
        }

        if (path.Equals(ModelsPath, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsGet(context.Request.Method)
                ? forwarder.WriteModelsAsync(context)
                : WriteMethodNotAllowedAsync(context, HttpMethods.Get);
        }

        return WriteNotFoundAsync(context);
    }

    private static async Task WriteMethodNotAllowedAsync(HttpContext context, string allowed)
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers[HeaderNames.Allow] = allowed;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsync(MethodNotAllowedBody, context.RequestAborted);
    }

    private static async Task WriteNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsync(NotFoundBody, context.RequestAborted);
    }
}
