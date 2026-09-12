namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
///     Registers the Docker Engine API routes the fake serves. Exactly the routes
///     <c>DockerDotNetRuntimeClient</c> calls and no others: a route nothing exercises is a route that can drift from
///     the real daemon with no test noticing.
///     <para>
///         The paths carry no <c>/v1.xx</c> prefix. That is not a simplification — the client does not send one; it
///         issues <c>POST /containers/create</c> and <c>GET /_ping</c> exactly as written here.
///     </para>
/// </summary>
internal static class FakeDockerEndpointMapper
{
    public static void MapFakeDockerEndpoints(this WebApplication app, FakeDockerState state)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(state);

        // Recorded here rather than in each endpoint: what a test asserts on is what the CLIENT sent, and a request
        // the fake has no route for is exactly the case worth seeing in that record.
        app.Use(async (context, next) =>
        {
            state.Record(new FakeDockerRequest(context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.Request.Query.ToDictionary(entry => entry.Key, entry => entry.Value.ToString(), StringComparer.Ordinal)));

            await next(context).ConfigureAwait(false);
        });

        app.MapGet("/_ping", PingEndpoint.Handle);
        app.MapMethods("/_ping", [HttpMethods.Head], PingEndpoint.Handle);
        app.MapGet("/version", (Delegate)((HttpContext context) => VersionEndpoint.HandleAsync(context, state)));
        app.MapGet("/info", (Delegate)((HttpContext context) => InfoEndpoint.HandleAsync(context, state)));
        app.MapPost("/images/create", (Delegate)((HttpContext context) => ImagePullEndpoint.HandleAsync(context, state)));
        app.MapGet("/images/{**reference}", (Delegate)((HttpContext context) => ImageInspectEndpoint.HandleAsync(context, state, RouteValue(context, "reference"))));

        app.MapPost("/containers/create", (Delegate)((HttpContext context) => ContainerCreateEndpoint.HandleAsync(context, state)));
        app.MapGet("/containers/json", (Delegate)((HttpContext context) => ContainerListEndpoint.HandleAsync(context, state)));
        app.MapPost("/containers/{id}/start", (Delegate)((HttpContext context) => ContainerStartEndpoint.HandleAsync(context, state, RouteValue(context, "id"))));
        app.MapPost("/containers/{id}/stop", (Delegate)((HttpContext context) => ContainerStopEndpoint.HandleAsync(context, state, RouteValue(context, "id"))));
        app.MapGet("/containers/{id}/json", (Delegate)((HttpContext context) => ContainerInspectEndpoint.HandleAsync(context, state, RouteValue(context, "id"))));
        app.MapGet("/containers/{id}/logs", (Delegate)((HttpContext context) => ContainerLogsEndpoint.HandleAsync(context, state, RouteValue(context, "id"))));
        app.MapDelete("/containers/{id}", (Delegate)((HttpContext context) => ContainerRemoveEndpoint.HandleAsync(context, state, RouteValue(context, "id"))));

        app.MapPost("/networks/create", (Delegate)((HttpContext context) => NetworkCreateEndpoint.HandleAsync(context, state)));
        app.MapGet("/networks", (Delegate)((HttpContext context) => NetworkListEndpoint.HandleAsync(context, state)));
        app.MapGet("/networks/{network}", (Delegate)((HttpContext context) => NetworkInspectEndpoint.HandleAsync(context, state, RouteValue(context, "network"))));
        app.MapDelete("/networks/{network}", (Delegate)((HttpContext context) => NetworkRemoveEndpoint.HandleAsync(context, state, RouteValue(context, "network"))));
        app.MapPost("/containers/{id}/exec", (Delegate)((HttpContext context) => ExecCreateEndpoint.HandleAsync(context, state, RouteValue(context, "id"))));
        app.MapPost("/exec/{id}/start", (Delegate)((HttpContext context) => ExecStartEndpoint.HandleAsync(context, state, RouteValue(context, "id"))));
        app.MapGet("/exec/{id}/json", (Delegate)((HttpContext context) => ExecInspectEndpoint.HandleAsync(context, state, RouteValue(context, "id"))));
    }

    /// <summary>
    ///     One route value as a string.
    ///     <para>
    ///         Read out of <c>RouteValues</c> rather than bound as a lambda parameter on purpose: a route handler
    ///         whose delegate declares a route parameter and is cast to <c>Delegate</c> makes the ASP.NET
    ///         <c>RouteHandlerAnalyzer</c> throw, which fails the Release build with AD0001. Every handler here
    ///         therefore takes the context alone, which is also the shape the fake Ollama server already uses.
    ///     </para>
    /// </summary>
    internal static string RouteValue(HttpContext context, string name)
    {
        return context.Request.RouteValues[name]?.ToString() ?? string.Empty;
    }

    /// <summary>Write one JSON document with the status code the daemon would use.</summary>
    internal static async Task WriteJsonAsync(HttpContext context, JsonNode body, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    ///     The daemon's own error shape: a status code and a <c>{"message": …}</c> body. Docker.DotNet turns the code
    ///     into the typed exception the production client catches, so the code is the load-bearing half.
    /// </summary>
    internal static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        return WriteJsonAsync(context,
            new JsonObject
            {
                ["message"] = message
            },
            statusCode);
    }

    /// <summary>The request body as a JSON object, or null when the request carried none.</summary>
    internal static async Task<JsonObject?> ReadJsonObjectAsync(HttpContext context)
    {
        if (context.Request.ContentLength is 0)
        {
            return null;
        }

        var node = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        return node as JsonObject;
    }

    /// <summary>
    ///     The <c>label</c> entries of a Docker filter document, which arrives as
    ///     <c>{"label":{"key=value":true}}</c> — a set rendered as a map of true, not a map of values.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseLabelFilter(string filters)
    {
        var required = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(filters) || JsonNode.Parse(filters)?["label"] is not JsonObject labels)
        {
            return required;
        }

        foreach (var (entry, _) in labels)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                required[entry[..separator]] = entry[(separator + 1)..];
            }
        }

        return required;
    }

    /// <summary>
    ///     Write frames in Docker's stdcopy framing: one 8-byte header per frame carrying the stream tag in byte 0
    ///     and the payload length as a big-endian int in bytes 4-7, then the payload.
    ///     <para>
    ///         <paramref name="hijacked" /> is what separates the two consumers. A log response is an ordinary body
    ///         the client reads to the end. An exec response is one the client takes the connection over for, and it
    ///         refuses to do that unless the response is neither chunked nor content-length delimited — which
    ///         <c>Transfer-Encoding: identity</c> is what produces, the host having no other way to be told not to
    ///         frame the body itself.
    ///     </para>
    /// </summary>
    internal static async Task WriteFramedAsync(HttpContext context, IEnumerable<FakeDockerLogFrame> frames, bool hijacked = false)
    {
        context.Response.ContentType = hijacked ? "application/vnd.docker.raw-stream" : "application/vnd.docker.multiplexed-stream";
        if (hijacked)
        {
            context.Response.Headers.TransferEncoding = "identity";
        }

        var header = new byte[8];
        foreach (var frame in frames)
        {
            var payload = Encoding.UTF8.GetBytes(frame.Text);
            if (payload.Length == 0)
            {
                continue;
            }

            Array.Clear(header);
            header[0] = (byte)frame.Stream;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(start: 4), payload.Length);

            await context.Response.Body.WriteAsync(header, context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
        }

        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>A network by name or by id — the client removes by id and inspects by name.</summary>
    internal static FakeDockerNetwork? FindNetwork(FakeDockerState state, string idOrName)
    {
        return state.Networks.TryGetValue(idOrName, out var byName)
            ? byName
            : state.Networks.Values.FirstOrDefault(network => string.Equals(network.Id, idOrName, StringComparison.Ordinal));
    }

    /// <summary>One network rendered as the daemon renders it, for both the inspect and the list routes.</summary>
    internal static JsonObject DescribeNetwork(FakeDockerNetwork network)
    {
        var labels = new JsonObject();
        foreach (var (key, value) in network.Labels)
        {
            labels[key] = value;
        }

        return new JsonObject
        {
            ["Name"] = network.Name,
            ["Id"] = network.Id,
            ["Scope"] = "local",
            ["Driver"] = network.Driver,
            ["Internal"] = network.Internal,
            ["Attachable"] = false,
            ["EnableIPv6"] = false,
            ["Labels"] = labels,
            ["Options"] = new JsonObject(),
            ["Containers"] = new JsonObject()
        };
    }
}
