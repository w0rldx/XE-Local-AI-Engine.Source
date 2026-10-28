namespace XE_Local_AI_Engine.Tests.Auth;

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The real identity store with a one-shot hook run right after the next refresh-token-by-hash read.</summary>
/// <remarks>
///     The hook gets whether that read ran inside an open transaction, so a test can force a competing write into the
///     gap between a caller's read and its own write without any timing.
/// </remarks>
internal sealed class RefreshTokenReadHookIdentityStore : INodeIdentityStore
{
    private readonly INodeIdentityStore _inner;
    private readonly NodeIdentityDbContext _dbContext;
    private readonly StrongBox<Func<bool, Task>?> _hook;

    public RefreshTokenReadHookIdentityStore(INodeIdentityStore inner, NodeIdentityDbContext dbContext, StrongBox<Func<bool, Task>?> hook)
    {
        _inner = inner;
        _dbContext = dbContext;
        _hook = hook;
    }

    public async Task<NodeRefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var token = await _inner.FindRefreshTokenAsync(tokenHash, cancellationToken);
        if (Interlocked.Exchange(ref _hook.Value, null) is { } hook)
        {
            await hook(_dbContext.Database.CurrentTransaction is not null);
        }

        return token;
    }

    public Task<bool> HasCompletedSetupAsync(CancellationToken cancellationToken) =>
        _inner.HasCompletedSetupAsync(cancellationToken);

    public Task<NodeUser?> FindCompletedSetupUserAsync(CancellationToken cancellationToken) =>
        _inner.FindCompletedSetupUserAsync(cancellationToken);

    public Task<IDbContextTransaction> BeginSerializableTransactionAsync(CancellationToken cancellationToken) =>
        _inner.BeginSerializableTransactionAsync(cancellationToken);

    public Task ReloadAsync(NodeUser user, CancellationToken cancellationToken) =>
        _inner.ReloadAsync(user, cancellationToken);

    public Task<NodeRefreshToken?> FindRefreshTokenByIdAsync(string id, CancellationToken cancellationToken) =>
        _inner.FindRefreshTokenByIdAsync(id, cancellationToken);

    public Task RotateAsync(NodeRefreshToken presented, NodeRefreshToken successor, DateTime now, CancellationToken cancellationToken) =>
        _inner.RotateAsync(presented, successor, now, cancellationToken);

    public Task AddRefreshTokenAsync(NodeRefreshToken token, CancellationToken cancellationToken) =>
        _inner.AddRefreshTokenAsync(token, cancellationToken);

    public Task RevokeActiveTokensAsync(string userId, DateTime now, CancellationToken cancellationToken) =>
        _inner.RevokeActiveTokensAsync(userId, now, cancellationToken);

    public Task RevokeRefreshTokenAsync(NodeRefreshToken token, DateTime now, CancellationToken cancellationToken) =>
        _inner.RevokeRefreshTokenAsync(token, now, cancellationToken);

    public Task AddSessionAsync(NodeAuthSession session, CancellationToken cancellationToken) =>
        _inner.AddSessionAsync(session, cancellationToken);

    public Task<NodeAuthSession?> FindSessionAsync(string id, CancellationToken cancellationToken) =>
        _inner.FindSessionAsync(id, cancellationToken);

    public Task<bool> IsSessionLiveAsync(string sessionId, string userId, CancellationToken cancellationToken) =>
        _inner.IsSessionLiveAsync(sessionId, userId, cancellationToken);

    public Task<IReadOnlyList<NodeAuthSession>> ListLiveSessionsAsync(string userId, DateTime now, CancellationToken cancellationToken) =>
        _inner.ListLiveSessionsAsync(userId, now, cancellationToken);

    public Task<bool> RevokeSessionAsync(string userId, string sessionId, DateTime now, CancellationToken cancellationToken) =>
        _inner.RevokeSessionAsync(userId, sessionId, now, cancellationToken);

    public Task MigrateAsync(CancellationToken cancellationToken) =>
        _inner.MigrateAsync(cancellationToken);

    public Task<bool> EnsureRoleAsync(string roleName, CancellationToken cancellationToken) =>
        _inner.EnsureRoleAsync(roleName, cancellationToken);
}
