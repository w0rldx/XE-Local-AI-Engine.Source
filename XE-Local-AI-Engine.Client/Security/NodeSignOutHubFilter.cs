namespace XE_Local_AI_Engine.Client.Security;

using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Keeps an open hub connection bound to its sign-in: registers it for abort at sign-out and re-runs the token's
///     revocation check on every invocation, since the JWT events see a hub token only at the handshake.
/// </summary>
/// <remarks>
///     The abort at sign-out stops server pushes; the per-invocation check refuses a call from a connection that abort
///     missed. Not an expiry check: a long chat stream outlives its access token by design.
/// </remarks>
public sealed class NodeSignOutHubFilter : IHubFilter
{
    private const string SignedOutMessage = "The sign-in session has ended. Sign in again.";

    private readonly NodeHubConnectionRegistry _registry;

    public NodeSignOutHubFilter(NodeHubConnectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);

        var caller = invocationContext.Context;
        if (await IsSignedOutAsync(caller, invocationContext.ServiceProvider))
        {
            caller.Abort();
            throw new HubException(SignedOutMessage);
        }

        return await next(invocationContext);
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var caller = context.Context;
        if (caller.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value is { Length: > 0 } userId)
        {
            _registry.Register(caller.ConnectionId, userId, caller.User.FindFirst(JwtRegisteredClaimNames.Sid)?.Value, caller.Abort);
        }

        try
        {
            // A sign-out that committed after the handshake check but before the registration above aborted nothing.
            if (await IsSignedOutAsync(caller, context.ServiceProvider))
            {
                throw new HubException(SignedOutMessage);
            }

            await next(context);
        }
        catch
        {
            // SignalR does not promise OnDisconnectedAsync after a failed OnConnected, so unregister here as well.
            _registry.Unregister(caller.ConnectionId);
            throw;
        }
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        _registry.Unregister(context.Context.ConnectionId);
        await next(context, exception);
    }

    private static async Task<bool> IsSignedOutAsync(HubCallerContext caller, IServiceProvider scopedServices) =>
        caller.User is { } user
        && await scopedServices.GetRequiredService<NodeSecurityStampCheck>().FindRevocationAsync(user, caller.ConnectionAborted) is not null;
}
