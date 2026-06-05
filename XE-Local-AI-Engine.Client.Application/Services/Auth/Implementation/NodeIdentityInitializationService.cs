namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;

public sealed class NodeIdentityInitializationService
{
    public const string AdminRoleName = "Admin";

    private readonly ILogger<NodeIdentityInitializationService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public NodeIdentityInitializationService(IServiceScopeFactory scopeFactory, ILogger<NodeIdentityInitializationService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task MigrateAndSeedAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>();

        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAdminRoleAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAdminRoleAsync(NodeIdentityDbContext dbContext, CancellationToken cancellationToken)
    {
        var normalizedRoleName = AdminRoleName.ToUpperInvariant();
        var roleExists = await dbContext.Roles
                                        .AnyAsync(role => role.NormalizedName == normalizedRoleName, cancellationToken)
                                        .ConfigureAwait(false);

        if (roleExists)
        {
            return;
        }

        dbContext.Roles.Add(new IdentityRole(AdminRoleName)
        {
            NormalizedName = normalizedRoleName,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Seeded node identity role {RoleName}.", AdminRoleName);
    }
}
