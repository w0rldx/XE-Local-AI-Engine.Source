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

    public Task<bool> HasActiveTokenCreatedAtAsync(string userId, DateTime createdAtUtc, DateTime now, CancellationToken cancellationToken)
    {
        return _dbContext.RefreshTokens
                         .AnyAsync(token => token.UserId == userId
                                            && token.RevokedAtUtc == null
                                            && token.ExpiresAtUtc > now
                                            && token.CreatedAtUtc == createdAtUtc,
                             cancellationToken);
    }

    public async Task RevokeAsync(NodeRefreshToken token, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        token.RevokedAtUtc = now;
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
        var activeTokens = await _dbContext.RefreshTokens
                                           .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
                                           .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.RevokedAtUtc = now;
        }

        // Nothing live means nothing to write: a save here would be a round-trip per logout that changed no row.
        if (activeTokens.Count > 0)
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
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
