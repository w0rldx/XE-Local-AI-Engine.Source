namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     One throwaway on-disk SQLite database per test, with both encryption interceptors wired, so a round-trip through
///     a fresh context proves the encrypt/decrypt path rather than the change tracker's in-memory plaintext.
/// </summary>
/// <remarks>
///     ponytail: one file per test for isolation. Only the encryption suite genuinely needs a real file — it scans and
///     mutates raw bytes — so if this namespace ever costs the memory-safe gate wall clock or handles, collapse the
///     rest to one fixture per class.
/// </remarks>
internal sealed class DevWorkflowTestFixture : IDisposable
{
    public const string SampleGraph = """{"schemaVersion":1,"nodes":[{"nodeKey":"research","nodeType":"Agent"}],"edges":[]}""";

    /// <summary>Both axes empty, which the resolver reads as "matches everything".</summary>
    public const string MatchAllScope = """{"projectIds":[],"nodeTypes":[]}""";

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-dev-workflows-" + Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_root, "dev-workflows.sqlite");

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

    /// <summary>A context with one extra interceptor wired, for tests that inject a competing write into a save.</summary>
    public NodeChatDbContext CreateContext(IInterceptor extraInterceptor) =>
        AgentDefinitionTestContextFactory.Create(DatabasePath, _keyHolder, extraInterceptor);

    public async Task<NodeChatDbContext> CreateSchemaAsync()
    {
        var context = CreateContext();
        _ = await context.Database.EnsureCreatedAsync();

        // This leaves a WAL database, not a rollback journal: EF Core's SqliteDatabaseCreator.Create enables WAL, and
        // journal_mode is a persistent file property. The encryption suite scans the MAIN database file and its
        // positive control — the plaintext title IS in the file — holds anyway, because it reads through
        // SqliteFileProbe.ReadAllBytesAsync, whose ClearAllPools closes the last connection and SQLite checkpoints the
        // log back into the main file on that close.
        return context;
    }

    public static DevWorkflowStore StoreFor(NodeChatDbContext context) =>
        new(context, TimeProvider.System);

    /// <summary>A work item, a definition and a run on it — the arrangement almost every test in this namespace needs.</summary>
    public static async Task<DevWorkflowSeed> SeedRunAsync(DevWorkflowStore store,
        string title = "Seeded work item",
        string request = "Seeded request",
        string graphJson = SampleGraph,
        Guid? developmentProjectId = null)
    {
        var workItem = await store.CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), title, request, developmentProjectId));
        var definition = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(), "Seeded definition", graphJson, NodeCount: 1));
        var run = await store.StartRunAsync(new StartDevWorkflowRunCommand(Guid.NewGuid(),
                                 workItem.Id,
                                 definition.Id,
                                 definition.Version,
                                 definition.GraphHash,
                                 graphJson));
        return new DevWorkflowSeed(workItem.Id, definition.Id, run.Id, run.Version);
    }

    /// <summary>A rule set scoped to everything, which is what most tests want one for.</summary>
    public static Task<DevWorkflowRuleSetSnapshot> CreateRuleSetAsync(DevWorkflowStore store,
        string name = "House rules",
        string body = "Always write the test first.",
        string scopeJson = MatchAllScope,
        bool enabled = true) =>
        store.CreateRuleSetAsync(new CreateDevWorkflowRuleSetCommand(Guid.NewGuid(), name, body, scopeJson, Enabled: enabled));

    /// <summary>Adds one node run to a seeded run and answers the run's post-commit version.</summary>
    public static async Task<long> AddNodeRunAsync(DevWorkflowStore store,
        Guid runId,
        Guid nodeRunId,
        string nodeKey,
        long expectedVersion,
        DevWorkflowNodeType nodeType = DevWorkflowNodeType.Agent,
        int maxAttempts = 3,
        string? inputJson = null,
        Guid? developmentProjectId = null)
    {
        var result = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(runId,
                                    expectedVersion,
                                    Guid.NewGuid(),
                                    [
                                        new DevWorkflowNodeRunSeed(nodeRunId,
                                            nodeKey,
                                            nodeType,
                                            maxAttempts,
                                            DevelopmentProjectId: developmentProjectId,
                                            InputJson: inputJson)
                                    ]));
        return result.Version;
    }

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

    public async Task<long> RawCountAsync(string table, string column, Guid value)
    {
        var count = await RawScalarAsync($"SELECT COUNT(*) FROM {table} WHERE {column} = $value;",
                command => command.Parameters.AddWithValue("$value", value));
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    public async Task<long> RawTableCountAsync(string table)
    {
        var count = await RawScalarAsync($"SELECT COUNT(*) FROM {table};");
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }
}

internal sealed record DevWorkflowSeed(Guid WorkItemId, Guid DefinitionId, Guid RunId, long RunVersion);
