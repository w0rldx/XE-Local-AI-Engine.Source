namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     One throwaway on-disk SQLite database per test, with both encryption interceptors wired, so a round-trip
///     through a fresh context proves the encrypt/decrypt path rather than the change tracker's in-memory plaintext.
/// </summary>
/// <remarks>
///     The run, node-run and event seeds go through the DbContext rather than a store: this slice ships the definition
///     half of <see cref="IGraphWorkflowStore" /> only, and the encryption and purge suites still have to cover every
///     column the migration creates. They move to the run store when it exists.
/// </remarks>
internal sealed class GraphWorkflowTestFixture : IDisposable
{
    /// <summary>
    ///     Opaque to this assembly: the parser and its rules live in the Application layer. Kept a graph that layer
    ///     would actually ACCEPT anyway — an End node needs its <c>outcome</c>, and a fixture whose sample document
    ///     the real parser refuses is one every later slice would have to work around.
    /// </summary>
    public const string SampleGraph =
        """{"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},{"key":"done","kind":"End","config":{"outcome":"completed"}}],"edges":[{"key":"e1","from":"start","to":"done"}]}""";

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-graph-workflows-" + Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_root, "graph-workflows.sqlite");

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
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);

        // Deliberately the rollback journal, as the Dev Workflow fixture leaves it. WAL holds recent writes in a -wal
        // sidecar until a checkpoint, and the encryption suite scans the MAIN database file: its positive control —
        // the plaintext name IS in the file — would then fail whenever no checkpoint had landed yet.
        return context;
    }

    public static GraphWorkflowStore StoreFor(NodeChatDbContext context) =>
        new(context, TimeProvider.System);

    public static Task<GraphWorkflowDefinitionSnapshot> SeedDefinitionAsync(GraphWorkflowStore store,
        string name = "Seeded definition",
        string graphJson = SampleGraph,
        int nodeCount = 2,
        string? description = null) =>
        store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand(Guid.NewGuid(), name, graphJson, nodeCount, SchemaVersion: 1, description));

    /// <summary>A run pinned to <paramref name="definitionId" />, written through the context so the interceptors run.</summary>
    public static async Task<Guid> SeedRunAsync(NodeChatDbContext context,
        Guid definitionId,
        GraphWorkflowRunStatus status = GraphWorkflowRunStatus.Running,
        string graphJson = SampleGraph,
        string? inputJson = null,
        string? outputJson = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = new GraphWorkflowRun
        {
            Id = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            DefinitionId = definitionId,
            DefinitionVersion = 1,
            GraphHash = "hash-" + Guid.NewGuid().ToString("N"),
            Status = status,
            FailureClass = GraphWorkflowFailureClass.None,
            GraphJson = Utf8(graphJson),
            InputJson = Utf8OrNull(inputJson),
            OutputJson = Utf8OrNull(outputJson),
            Seq = 0,
            Version = 1,
            CreatedAtUtc = 1
        };
        _ = context.GraphWorkflowRuns.Add(run);
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        return run.Id;
    }

    public static async Task<Guid> SeedNodeRunAsync(NodeChatDbContext context,
        Guid runId,
        string nodeKey = "start",
        GraphWorkflowNodeKind kind = GraphWorkflowNodeKind.Start,
        GraphWorkflowNodeRunStatus status = GraphWorkflowNodeRunStatus.Pending,
        string? inputJson = null,
        string? outputJson = null,
        string? error = null,
        string? decidedBySubject = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var nodeRun = new GraphWorkflowNodeRun
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeKey = nodeKey,
            Kind = kind,
            Status = status,
            Attempt = 1,
            FailureClass = GraphWorkflowFailureClass.None,
            InputJson = Utf8OrNull(inputJson),
            OutputJson = Utf8OrNull(outputJson),
            Error = Utf8OrNull(error),
            DecidedBySubject = Utf8OrNull(decidedBySubject),
            UpdatedAtUtc = 1
        };
        _ = context.GraphWorkflowNodeRuns.Add(nodeRun);
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        return nodeRun.Id;
    }

    public static async Task<Guid> SeedRunEventAsync(NodeChatDbContext context, Guid runId, long seq, string eventType = "run.started", string? detailJson = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var runEvent = new GraphWorkflowRunEvent
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Seq = seq,
            EventType = eventType,
            DetailJson = Utf8OrNull(detailJson),
            CreatedAtUtc = 1
        };
        _ = context.GraphWorkflowRunEvents.Add(runEvent);
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        return runEvent.Id;
    }

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

    public async Task<long> RawTableCountAsync(string table)
    {
        var count = await RawScalarAsync($"SELECT COUNT(*) FROM {table};").ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    private static byte[] Utf8(string value) =>
        System.Text.Encoding.UTF8.GetBytes(value);

    private static byte[]? Utf8OrNull(string? value) =>
        value is null ? null : System.Text.Encoding.UTF8.GetBytes(value);
}
