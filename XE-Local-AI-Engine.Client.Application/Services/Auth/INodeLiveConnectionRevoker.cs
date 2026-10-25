namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Ends the long-lived connections (SignalR hubs) a sign-out has just invalidated. Their token was checked once, at
///     connect, so a revocation the database already holds does not reach them on its own.
/// </summary>
/// <remarks>Call it only after the revoking write has committed: an aborted client reconnects at once and must be refused.</remarks>
public interface INodeLiveConnectionRevoker
{
    /// <summary>Aborts every connection of <paramref name="userId" /> whose token names session <paramref name="sessionId" />.</summary>
    void AbortSession(string userId, string sessionId);

    /// <summary>Aborts every connection of <paramref name="userId" />, including those whose token names no session.</summary>
    void AbortUser(string userId);
}
