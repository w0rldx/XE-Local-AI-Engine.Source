namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     EF-backed <see cref="INodeIdentityStore" />, scoped to the DbContext lifetime.
/// </summary>
/// <remarks>
///     Plain LINQ over <see cref="NodeIdentityDbContext" />: unlike the chat and knowledge tables there is nothing
///     here that EF cannot express, so no raw ADO appears. Reads that feed an Identity write stay TRACKED on purpose
///     — see the interface.
/// </remarks>
public sealed class NodeIdentityStore : INodeIdentityStore
{
    private readonly NodeIdentityDbContext _dbContext;

    public NodeIdentityStore(NodeIdentityDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<bool> HasCompletedSetupAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Users.AnyAsync(user => user.SetupCompleted, cancellationToken);
    }

    public Task<NodeUser?> FindCompletedSetupUserAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Users.SingleOrDefaultAsync(user => user.SetupCompleted, cancellationToken);
    }

    public async Task<IDbContextTransaction> BeginSerializableTransactionAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    }

    public Task ReloadAsync(NodeUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _dbContext.Entry(user).ReloadAsync(cancellationToken);
    }

    public Task<NodeRefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken)
    {
        return _dbContext.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);
    }

    public Task<NodeRefreshToken?> FindRefreshTokenByIdAsync(string id, CancellationToken cancellationToken)
    {
        return _dbContext.RefreshTokens.SingleOrDefaultAsync(token => token.Id == id, cancellationToken);
    }

    public async Task RotateAsync(NodeRefreshToken presented, NodeRefreshToken successor, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(successor);

        presented.RevokedAtUtc = now;
        presented.ReplacedByTokenId = successor.Id;
        _ = _dbContext.RefreshTokens.Add(successor);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRefreshTokenAsync(NodeRefreshToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        _ = _dbContext.RefreshTokens.Add(token);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeActiveTokensAsync(string userId, DateTime now, CancellationToken cancellationToken)
    {
        // Set-based, never load-then-save: a refresh committing in between would leave its new token and session live.
        // Tokens go first, so a refresh racing the second statement already finds its token revoked.
        _ = await _dbContext.RefreshTokens
                            .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAtUtc, now), cancellationToken);
        _ = await _dbContext.AuthSessions
                            .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAtUtc, now), cancellationToken);
    }

    public async Task RevokeRefreshTokenAsync(NodeRefreshToken token, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        token.RevokedAtUtc ??= now;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddSessionAsync(NodeAuthSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        _ = _dbContext.AuthSessions.Add(session);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<NodeAuthSession?> FindSessionAsync(string id, CancellationToken cancellationToken)
    {
        return _dbContext.AuthSessions.SingleOrDefaultAsync(session => session.Id == id, cancellationToken);
    }

    public Task<bool> IsSessionLiveAsync(string sessionId, string userId, CancellationToken cancellationToken)
    {
        return _dbContext.AuthSessions
                         .AsNoTracking()
                         .AnyAsync(session => session.Id == sessionId && session.UserId == userId && session.RevokedAtUtc == null,
                             cancellationToken);
    }

    public async Task<IReadOnlyList<NodeAuthSession>> ListLiveSessionsAsync(string userId, DateTime now, CancellationToken cancellationToken)
    {
        return await _dbContext.AuthSessions
                               .AsNoTracking()
                               .Where(session => session.UserId == userId
                                                 && session.RevokedAtUtc == null
                                                 && _dbContext.RefreshTokens.Any(token => token.SessionId == session.Id
                                                                                          && token.RevokedAtUtc == null
                                                                                          && token.ExpiresAtUtc > now))
                               .OrderByDescending(session => session.LastUsedAtUtc)
                               .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeSessionAsync(string userId, string sessionId, DateTime now, CancellationToken cancellationToken)
    {
        var session = await _dbContext.AuthSessions
                                      .SingleOrDefaultAsync(candidate => candidate.Id == sessionId && candidate.UserId == userId,
                                          cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.RevokedAtUtc ??= now;
        var liveTokens = await _dbContext.RefreshTokens
                                         .Where(token => token.SessionId == sessionId && token.RevokedAtUtc == null)
                                         .ToListAsync(cancellationToken);
        foreach (var token in liveTokens)
        {
            token.RevokedAtUtc = now;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task MigrateAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Database.MigrateAsync(cancellationToken);
    }

    public async Task<bool> EnsureRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);

        var normalizedRoleName = roleName.ToUpperInvariant();
        var roleExists = await _dbContext.Roles
                                         .AnyAsync(role => role.NormalizedName == normalizedRoleName, cancellationToken);

        if (roleExists)
        {
            return false;
        }

        _ = _dbContext.Roles.Add(new IdentityRole(roleName)
        {
            NormalizedName = normalizedRoleName,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });

        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
