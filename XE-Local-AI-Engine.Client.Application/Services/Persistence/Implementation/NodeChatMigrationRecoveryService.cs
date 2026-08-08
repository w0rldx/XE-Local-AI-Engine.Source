namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;

public sealed class NodeChatMigrationRecoveryService
{
    private const string ConnectionStringName = "node-sqlite";
    private const string EfMigrationsLockTableName = "__EFMigrationsLock";

    private readonly IConfiguration _configuration;
    private readonly ILogger<NodeChatMigrationRecoveryService> _logger;
    private readonly NodeChatMigrationRecoveryOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;

    public NodeChatMigrationRecoveryService(IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IOptions<NodeChatMigrationRecoveryOptions> options,
        ILogger<NodeChatMigrationRecoveryService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = _configuration.GetConnectionString(ConnectionStringName)
                               ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is required.");

        using var startupLock = await AcquireStartupLockAsync(connectionString, cancellationToken).ConfigureAwait(false);

        if (await TryMigrateOnceAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogWarning("Node SQLite migration attempt timed out after {Timeout}. Recovering possible stale {LockTable} table before one retry.",
            _options.MigrationAttemptTimeout,
            EfMigrationsLockTableName);

        await DropEfMigrationsLockTableAsync(cancellationToken).ConfigureAwait(false);

        if (!await TryMigrateOnceAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException($"Node SQLite migration still did not complete within {_options.MigrationAttemptTimeout} after clearing {EfMigrationsLockTableName}.");
        }
    }

    private async Task<bool> TryMigrateOnceAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ValidatePositive(_options.MigrationAttemptTimeout, nameof(NodeChatMigrationRecoveryOptions.MigrationAttemptTimeout)));

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

            await dbContext.Database.MigrateAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task DropEfMigrationsLockTableAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        await dbContext.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS \"{EfMigrationsLockTableName}\";",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileStream?> AcquireStartupLockAsync(string connectionString, CancellationToken cancellationToken)
    {
        var lockPath = ResolveStartupLockPath(connectionString);
        if (lockPath is null)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        var timeout = ValidatePositive(_options.StartupLockTimeout, nameof(NodeChatMigrationRecoveryOptions.StartupLockTimeout));
        var pollInterval = ValidatePositive(_options.StartupLockPollInterval, nameof(NodeChatMigrationRecoveryOptions.StartupLockPollInterval));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lockFile = TryOpenStartupLock(lockPath);
            if (lockFile is not null)
            {
                return lockFile;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"Could not acquire node SQLite migration startup lock '{lockPath}'. Another node process may be applying migrations.");
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static FileStream? TryOpenStartupLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ResolveStartupLockPath(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var databasePath = Path.GetFullPath(dataSource);
        return databasePath + ".migration.lock";
    }

    private static TimeSpan ValidatePositive(TimeSpan value, string optionName)
    {
        return value > TimeSpan.Zero
            ? value
            : throw new InvalidOperationException($"{optionName} must be greater than zero.");
    }
}
