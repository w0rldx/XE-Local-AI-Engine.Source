namespace XE_Local_AI_Engine.Client.Security;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Every open hub connection with the user and session its token named, so a sign-out can abort exactly the connections
///     it invalidated. Filled and emptied by <see cref="NodeSignOutHubFilter" />.
/// </summary>
/// <remarks>Accepted ceiling: an abort scans every open connection; a single-operator node holds a few dozen at most.</remarks>
public sealed class NodeHubConnectionRegistry : INodeLiveConnectionRevoker
{
    private readonly ConcurrentDictionary<string, Registration> _connections = new(StringComparer.Ordinal);

    public void Register(string connectionId, string userId, string? sessionId, Action abort)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(abort);

        _connections[connectionId] = new Registration
        {
            UserId = userId,
            SessionId = sessionId,
            Abort = abort
        };
    }

    public void Unregister(string connectionId) =>
        _connections.TryRemove(connectionId, out _);

    public void AbortSession(string userId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        AbortWhere(registration => string.Equals(registration.UserId, userId, StringComparison.Ordinal)
                                   && string.Equals(registration.SessionId, sessionId, StringComparison.Ordinal));
    }

    public void AbortUser(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        AbortWhere(registration => string.Equals(registration.UserId, userId, StringComparison.Ordinal));
    }

    private void AbortWhere(Func<Registration, bool> matches)
    {
        // Enumerating a ConcurrentDictionary is safe beside concurrent adds and removes; Abort only signals the close, and
        // the disconnect that follows unregisters the connection.
        foreach (var registration in _connections.Values.Where(matches))
        {
            registration.Abort();
        }
    }

    private sealed class Registration
    {
        public required string UserId { get; init; }

        public required string? SessionId { get; init; }

        public required Action Abort { get; init; }
    }
}
