namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

using System.Net;
using System.Security.Cryptography;
using System.Text;

public static class HostAgentLinuxAdminEndpoints
{
    public static void UseLocalAdminRequestGuards(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!IsLoopbackRequest(context) || !HasAllowedHost(context) || HasOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    public static void MapLocalAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/status", async (HttpContext context,
            HostAgentAdminTokenStore tokenStore,
            HostAgentLinuxAdminService adminService,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, tokenStore, cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await adminService.GetStatusAsync(cancellationToken).ConfigureAwait(false));
        });

        app.MapGet("/logs", async (HttpContext context,
            HostAgentAdminTokenStore tokenStore,
            HostAgentLinuxAdminService adminService,
            int? tail,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, tokenStore, cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new HostAgentLinuxLogTail(await adminService.ReadLogsAsync(tail ?? 200, cancellationToken).ConfigureAwait(false)));
        });

        app.MapPost("/shutdown", async (HttpContext context,
            HostAgentAdminTokenStore tokenStore,
            HostAgentLinuxAdminService adminService,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, tokenStore, cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await adminService.ShutdownAsync(cancellationToken).ConfigureAwait(false));
        });

        app.MapPost("/startup", async (HttpContext context,
            HostAgentAdminTokenStore tokenStore,
            HostAgentLinuxAdminService adminService,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, tokenStore, cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await adminService.StartupAsync(cancellationToken).ConfigureAwait(false));
        });

        app.MapPost("/restart", async (HttpContext context,
            HostAgentAdminTokenStore tokenStore,
            HostAgentLinuxAdminService adminService,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, tokenStore, cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await adminService.RestartAsync(cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<bool> IsAuthorizedAsync(HttpContext context,
        HostAgentAdminTokenStore tokenStore,
        CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(context, out var token))
        {
            return false;
        }

        var expectedToken = await tokenStore.GetOrCreateAdminTokenAsync(cancellationToken).ConfigureAwait(false);
        return FixedTimeEquals(token, expectedToken);
    }

    private static bool TryGetBearerToken(HttpContext context, out string token)
    {
        const string bearerPrefix = "Bearer ";
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith(bearerPrefix, StringComparison.Ordinal) && authorization.Length > bearerPrefix.Length)
        {
            token = authorization[bearerPrefix.Length..];
            return true;
        }

        token = string.Empty;
        return false;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        return remoteIp is null || IPAddress.IsLoopback(remoteIp);
    }

    private static bool HasAllowedHost(HttpContext context)
    {
        var host = context.Request.Host.Host;
        return string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
               || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOrigin(HttpContext context)
    {
        return !string.IsNullOrWhiteSpace(context.Request.Headers.Origin.ToString());
    }
}

public sealed record HostAgentLinuxLogTail(IReadOnlyList<string> Lines);
