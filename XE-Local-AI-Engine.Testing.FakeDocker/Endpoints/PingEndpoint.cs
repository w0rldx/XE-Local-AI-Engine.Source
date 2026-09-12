namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /_ping</c>. The daemon answers <c>OK</c> as plain text; the client reads only whether it answered at
///     all, which is why this is the one route with no state behind it.
/// </summary>
internal static class PingEndpoint
{
    public static IResult Handle()
    {
        return Results.Text("OK", "text/plain");
    }
}
