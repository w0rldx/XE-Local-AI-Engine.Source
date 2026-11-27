namespace XE_Local_AI_Engine.Client.Persistence.Tests.ModelLab;

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class ModelLabPlaygroundRunStoreTests : IDisposable
{
    private const string Summary = """{"formatVersion":1,"template":"tiny-mlp-v1","secret":"summary-plaintext"}""";

    private readonly INodeSqliteKeyHolder _keyHolder = new FixedNodeSqliteKeyHolder(RandomNumberGenerator.GetBytes(32));
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_rootPath, "playground.sqlite");

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task TheSummary_IsCiphertextAtRest_AndAFreshContextReadsItBackDecrypted()
    {
        var id = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        await using (var context = await CreateDatabaseAsync())
        {
            var created = await new ModelLabPlaygroundRunStore(context, TimeProvider.System).CreateAsync(Create(id, blobId));
            AssertEx.Equal(Summary, Encoding.UTF8.GetString(created.ResultJson.Span));

            var raw = AssertEx.NotNull(await ReadRawAsync(context, "result_json"));
            AssertEx.False(Encoding.UTF8.GetString(raw).Contains("summary-plaintext", StringComparison.Ordinal), "result_json is stored as plaintext.");
        }

        await using var fresh = AgentDefinitionTestContextFactory.Create(DatabasePath, _keyHolder);
        var record = AssertEx.NotNull(await new ModelLabPlaygroundRunStore(fresh, TimeProvider.System).GetAsync(id));
        AssertEx.Equal(Summary, Encoding.UTF8.GetString(record.ResultJson.Span));
        AssertEx.Equal("tiny-mlp-v1", record.TemplateId);
        AssertEx.Equal(blobId, record.TraceBlobId);
        AssertEx.Equal(new string('a', 64), record.TraceSha256);
        AssertEx.Equal(expected: 42L, record.TraceByteCount);
    }

    [Test]
    public async Task List_IsNewestFirst_AndDelete_ReportsWhetherARowWent()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabPlaygroundRunStore(context, new SteppingTimeProvider());
        var older = await CreateAsync(store);
        var newer = await CreateAsync(store);

        AssertEx.Equal($"{newer},{older}", string.Join(',', (await store.ListAsync()).Select(static run => run.Id)));
        AssertEx.True(await store.DeleteAsync(older));
        AssertEx.False(await store.DeleteAsync(older), "A second delete removes nothing.");
        AssertEx.Null(await store.GetAsync(older));
        AssertEx.Equal(newer, (await store.ListAsync()).Single().Id);
    }

    [Test]
    public async Task Trim_KeepsTheNewest_AndReturnsOnlyTheRowsItRemoved()
    {
        var interceptor = new DeleteTheFirstTargetFirst();
        await using var context = await CreateDatabaseAsync(interceptor);
        var store = new ModelLabPlaygroundRunStore(context, new SteppingTimeProvider());
        var ids = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add(await CreateAsync(store));
        }

        interceptor.Armed = true;
        var removed = await store.TrimToNewestAsync(keep: 2);

        AssertEx.Equal(expected: 1, interceptor.Deleted, "Another delete took the first target before the trim reached it.");
        AssertEx.Equal(expected: 2, removed.Count);
        AssertEx.True(removed.All(id => ids.Take(3).Contains(id)), "Only the oldest rows go.");
        AssertEx.Equal($"{ids[4]},{ids[3]}", string.Join(',', (await store.ListAsync()).Select(static run => run.Id)));
        AssertEx.Empty(await store.TrimToNewestAsync(keep: 2));
    }

    [Test]
    public async Task Trim_ThatFailsPartWay_DeletesNothing()
    {
        var failSecond = new FailSecondDelete();
        await using var context = await CreateDatabaseAsync(failSecond);
        var store = new ModelLabPlaygroundRunStore(context, new SteppingTimeProvider());
        for (var index = 0; index < 3; index++)
        {
            _ = await CreateAsync(store);
        }

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => store.TrimToNewestAsync(keep: 1));

        AssertEx.Equal(expected: 2, failSecond.Deletes, "The first delete ran before the second failed.");
        AssertEx.Equal(expected: 3, (await store.ListAsync()).Count, "The first delete was rolled back, so no row is left without its scope's cleanup.");
    }

    private static ModelLabPlaygroundRunCreate Create(Guid id, Guid blobId) =>
        new()
        {
            Id = id,
            TemplateId = "tiny-mlp-v1",
            TraceBlobId = blobId,
            TraceSha256 = new string('a', 64),
            TraceByteCount = 42,
            ResultJson = Encoding.UTF8.GetBytes(Summary)
        };

    private static async Task<Guid> CreateAsync(ModelLabPlaygroundRunStore store)
    {
        var id = Guid.NewGuid();
        _ = await store.CreateAsync(Create(id, Guid.NewGuid()));
        return id;
    }

    private static async Task<byte[]?> ReadRawAsync(NodeChatDbContext context, string column)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The column is one of this test's constants.
        command.CommandText = $"SELECT {column} FROM model_lab_playground_runs";
#pragma warning restore CA2100
        return await command.ExecuteScalarAsync() as byte[];
    }

    private async Task<NodeChatDbContext> CreateDatabaseAsync(IInterceptor? extraInterceptor = null)
    {
        _ = Directory.CreateDirectory(_rootPath);
        await MigratedDatabaseTemplate.CopyChatHeadAsync(DatabasePath);
        var context = extraInterceptor is null
            ? AgentDefinitionTestContextFactory.Create(DatabasePath, _keyHolder)
            : AgentDefinitionTestContextFactory.Create(DatabasePath, _keyHolder, extraInterceptor);
        await context.Database.OpenConnectionAsync();
        return context;
    }

    /// <summary>Once armed, deletes the target of the trim's first delete just before that delete runs, as a concurrent delete would.</summary>
    private sealed class DeleteTheFirstTargetFirst : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public int Deleted { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (Armed && command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Armed = false;
                var target = (SqliteParameter)command.Parameters.Cast<DbParameter>().Single();
                await using var delete = ((SqliteConnection)command.Connection!).CreateCommand();
                delete.CommandText = "DELETE FROM model_lab_playground_runs WHERE id = $id";
                _ = delete.Parameters.Add(new SqliteParameter("$id", target.SqliteType)
                {
                    Value = target.Value
                });
                Deleted = await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FailSecondDelete : DbCommandInterceptor
    {
        public int Deletes { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) && ++Deletes == 2)
            {
                throw new InvalidOperationException("database is locked");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
