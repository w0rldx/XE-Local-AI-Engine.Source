namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     One throwaway on-disk SQLite database per test, with both encryption interceptors wired, so a round-trip through
///     a fresh context proves the encrypt/decrypt path rather than the change tracker's in-memory plaintext.
/// </summary>
internal sealed class WorkSessionTestFixture : IDisposable
{
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-work-sessions-" + Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_root, "work-sessions.sqlite");

    public string Root => _root;

    public void Dispose()
    {
        _keyHolder.Dispose();
        SqliteFileProbe.ReleasePooledHandles();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public NodeChatDbContext CreateContext() =>
        AgentDefinitionTestContextFactory.Create(DatabasePath, _keyHolder);

    /// <summary>Creates the schema and returns the context that created it.</summary>
    public async Task<NodeChatDbContext> CreateSchemaAsync()
    {
        var context = CreateContext();
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return context;
    }

    public static AgentWorkSessionStore StoreFor(NodeChatDbContext context) =>
        new(context, TimeProvider.System);

    /// <summary>Creates a session and returns the store snapshot plus the store that made it.</summary>
    public static Task<AgentWorkSessionSnapshot> SeedAsync(AgentWorkSessionStore store,
        Guid sessionId,
        string title = "Seeded session",
        string objective = "Seeded objective",
        AgentWorkSessionKind kind = AgentWorkSessionKind.Research) =>
        store.CreateAsync(CreateSeed(sessionId, title, objective, kind));

    public static CreateWorkSessionCommand CreateSeed(Guid sessionId,
        string title = "Seeded session",
        string objective = "Seeded objective",
        AgentWorkSessionKind kind = AgentWorkSessionKind.Research) =>
        new(sessionId, Guid.NewGuid(), Guid.NewGuid(), kind, title, objective);

    /// <summary>Runs a scalar query straight against the file, for assertions the entity model would false-pass.</summary>
    public async Task<object?> RawScalarAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is a fixed literal in the calling suite; every value binds through `configure`.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    public async Task RawExecuteAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Same: fixed literal, bound parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<long> RawCountAsync(string table, string column, Guid value)
    {
        var count = await RawScalarAsync($"SELECT COUNT(*) FROM {table} WHERE {column} = $value;",
                command => command.Parameters.AddWithValue("$value", value))
            .ConfigureAwait(false);
        return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture);
    }
}
