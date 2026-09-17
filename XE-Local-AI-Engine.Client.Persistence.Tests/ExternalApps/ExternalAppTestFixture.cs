namespace XE_Local_AI_Engine.Client.Persistence.Tests.ExternalApps;

using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     One throwaway on-disk SQLite database per test with both encryption interceptors wired, so a round trip through
///     a fresh context proves the encrypt/decrypt path rather than the change tracker's in-memory plaintext. Shaped on
///     <c>IntegrationTestFixture</c>. A real file rather than an in-memory database, because several suites here read
///     the stored bytes back with raw SQL — the only way to prove a column is sealed at rest.
/// </summary>
internal sealed class ExternalAppTestFixture : IDisposable
{
    /// <summary>The manifest snapshot every seeded instance is installed with. Content is irrelevant; identity is not.</summary>
    public const string SeedManifestJson = """{"applicationId":"odysseus","manifestVersion":1}""";

    /// <summary>A variables document with a value no other string in these suites contains.</summary>
    public const string SeedVariablesJson = """{"ODYSSEUS_ADMIN_PASSWORD":"correct-horse-battery-staple"}""";

    /// <summary>The secret inside <see cref="SeedVariablesJson" />, searched for in the raw file and in every ToString.</summary>
    public const string SeedSecret = "correct-horse-battery-staple";

    /// <summary>The secret half of every seeded bridge token, searched for in the raw file and in every ToString.</summary>
    public const string SeedBridgeSecret = "bridge-secret-material-not-a-real-token";

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-external-apps-" + Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_root, "external-apps.sqlite");

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

    public async Task<NodeChatDbContext> CreateSchemaAsync()
    {
        var context = CreateContext();
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }

    public static ExternalAppInstanceCreate Create(Guid? id = null,
        string applicationId = "odysseus",
        string variablesJson = SeedVariablesJson,
        ExternalAppInstanceEventKind firstEventKind = ExternalAppInstanceEventKind.PermissionAccepted,
        string? firstEventDetailJson = null,
        long createdAtUtc = 1_000,
        bool withBridgeToken = true)
    {
        var instanceId = id ?? Guid.NewGuid();
        return new ExternalAppInstanceCreate(instanceId,
            applicationId,
            ManifestVersion: 1,
            SeedManifestJson,
            DisplayName: "Odysseus",
            variablesJson,
            StoragePath: "/var/lib/xe/external-apps/instances/0",
            RuntimeProvider: "docker",
            RuntimeOverride: null,
            createdAtUtc,
            firstEventKind,
            firstEventDetailJson,
            // `false` models the one row shape that legitimately has none: an instance installed before the bridge
            // existed. Every install this engine performs mints one.
            withBridgeToken ? BridgeTokenFor(instanceId) : null);
    }

    /// <summary>A seeded instance's bridge token, in the real token's shape so the id half addresses its own row.</summary>
    public static string BridgeTokenFor(Guid instanceId)
    {
        return instanceId.ToString("N") + "." + SeedBridgeSecret;
    }

    /// <summary>
    ///     A status transition with the two fields every caller must supply and nothing else, so each test's own
    ///     arguments are the only thing that varies from the default.
    /// </summary>
    public static ExternalAppStatusUpdate Transition(Guid instanceId,
        long expectedVersion,
        ExternalAppInstanceStatus newStatus,
        ExternalAppInstanceStatus expectedStatus,
        ExternalAppInstanceEventKind eventKind = ExternalAppInstanceEventKind.Started,
        long occurredAtUtc = 2_000) =>
        new(instanceId,
            expectedVersion,
            new HashSet<ExternalAppInstanceStatus>
            {
                expectedStatus
            },
            newStatus,
            eventKind,
            EventDetailJson: null,
            occurredAtUtc);

    /// <summary>Runs a scalar query straight against the file, for assertions the entity model would false-pass.</summary>
    public async Task<object?> RawScalarAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is a fixed literal in the calling suite; every value binds through `configure`.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public async Task RawExecuteAsync(string sql, Action<SqliteCommand>? configure = null)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Same: fixed literal, bound parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        configure?.Invoke(command);
        _ = await command.ExecuteNonQueryAsync();
    }

    public async Task<long> RawTableCountAsync(string table)
    {
        var count = await RawScalarAsync($"SELECT COUNT(*) FROM {table};");
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }
}
