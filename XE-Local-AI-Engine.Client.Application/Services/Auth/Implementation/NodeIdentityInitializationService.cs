namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

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
        var identity = scope.ServiceProvider.GetRequiredService<INodeIdentityStore>();

        await identity.MigrateAsync(cancellationToken);

        if (await identity.EnsureRoleAsync(AdminRoleName, cancellationToken))
        {
            _logger.LogInformation("Seeded node identity role {RoleName}.", AdminRoleName);
        }
    }
}
