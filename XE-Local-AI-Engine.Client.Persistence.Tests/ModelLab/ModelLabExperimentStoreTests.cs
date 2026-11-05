namespace XE_Local_AI_Engine.Client.Persistence.Tests.ModelLab;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class ModelLabExperimentStoreTests : IDisposable
{
    private const string Prompt = "the secret prompt under test";

    private readonly INodeSqliteKeyHolder _keyHolder = new FixedKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task Progress_SurvivesARestartAndAFailure_AndIsCiphertextAtRest()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        await store.CreateRunningAsync(new ModelLabExperimentCreate
        {
            Id = id,
            Kind = ModelLabExperimentKind.Steering,
            RepoId = "org/repo",
            Revision = "rev",
            BaseArtifactId = Guid.NewGuid(),
            InstalledModelName = "org/model:Q4",
            Prompt = Prompt,
            LicenseConfirmationJson = new byte[]
            {
                1
            }
        });

        await store.SetProgressAsync(id, Encoding.UTF8.GetBytes("arm-one-plaintext"), new ModelLabBlobReference
        {
            BlobId = blobId,
            Sha256 = "hash",
            ByteCount = 7
        });
        await store.SetProgressAsync(id, Encoding.UTF8.GetBytes("arm-two-plaintext"), blob: null);
        await store.RecoverOnStartupAsync();
        context.ChangeTracker.Clear();

        var read = (await store.GetAsync(id))!;
        AssertEx.Equal(ModelLabExperimentKind.Steering, read.Kind);
        AssertEx.Equal(ModelLabExperimentStatus.Failed, read.Status);
        AssertEx.Equal("arm-two-plaintext", Encoding.UTF8.GetString(read.ResultJson!.Value.Span));
        AssertEx.Equal(blobId, read.TraceBlobId!.Value);
        AssertEx.Equal(expected: 7L, read.TraceByteCount!.Value);

        await store.SetProgressAsync(id, Encoding.UTF8.GetBytes("after-the-end"), blob: null);
        context.ChangeTracker.Clear();
        AssertEx.Equal("arm-two-plaintext", Encoding.UTF8.GetString((await store.GetAsync(id))!.ResultJson!.Value.Span));
    }

    [Test]
    public async Task ACompletionWithNoResult_LeavesTheColumnNull_AndReadsBackAsNone()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = Guid.NewGuid();
        await store.CreateRunningAsync(Create(id));

        await store.CompleteAsync(id, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Failed,
            ErrorMessage = "failed"
        });
        context.ChangeTracker.Clear();

        AssertEx.Null((await context.ModelLabExperiments.AsNoTracking().SingleAsync(item => item.Id == id)).ResultJson);
        AssertEx.False((await store.GetAsync(id))!.ResultJson.HasValue);
    }

    [Test]
    public async Task ACompletion_KeepsWhatTheRowAlreadyHolds()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = Guid.NewGuid();
        await store.CreateRunningAsync(Create(id));
        await store.SetProgressAsync(id, Encoding.UTF8.GetBytes("one-arm"), blob: null);

        await store.CompleteAsync(id, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Cancelled,
            ErrorMessage = "cancelled"
        });
        context.ChangeTracker.Clear();

        var read = (await store.GetAsync(id))!;
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, read.Status);
        AssertEx.Equal("one-arm", Encoding.UTF8.GetString(read.ResultJson!.Value.Span));
        AssertEx.Equal(ModelLabExperimentKind.Trace, read.Kind, "A row that does not say is a trace.");
    }

    [Test]
    public async Task EveryPrivateColumn_IsCiphertextAtRest_AndReadsBackDecrypted()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = Guid.NewGuid();
        await store.CreateRunningAsync(Create(id));
        await store.SetLaunchReceiptAsync(id, Encoding.UTF8.GetBytes("receipt-plaintext"));
        await store.CompleteAsync(id, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Failed,
            ErrorMessage = "error-plaintext",
            ResultJson = Encoding.UTF8.GetBytes("result-plaintext")
        });
        await store.SetLaunchReceiptAsync(id, Encoding.UTF8.GetBytes("receipt-plaintext"));
        context.ChangeTracker.Clear();

        var plaintextByColumn = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompt"] = Prompt,
            ["error_message"] = "error-plaintext",
            ["result_json"] = "result-plaintext",
            ["launch_receipt_json"] = "receipt-plaintext",
            ["license_confirmation_json"] = "license-plaintext"
        };
        foreach (var (column, plaintext) in plaintextByColumn)
        {
            var raw = AssertEx.NotNull(await ReadRawAsync(context, column), column);
            AssertEx.False(Encoding.UTF8.GetString(raw).Contains(plaintext, StringComparison.Ordinal), $"{column} is stored as plaintext.");
        }

        var record = AssertEx.NotNull(await store.GetAsync(id));
        AssertEx.Equal(Prompt, record.Prompt);
        AssertEx.Equal("error-plaintext", record.ErrorMessage!);
        AssertEx.Equal("result-plaintext", Encoding.UTF8.GetString(record.ResultJson!.Value.Span));
    }

    [Test]
    public async Task Complete_ClearsTheReceipt_AndOnlyMovesARunningRow()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = Guid.NewGuid();
        await store.CreateRunningAsync(Create(id));
        await store.SetLaunchReceiptAsync(id, new byte[]
        {
            1
        });

        await store.CompleteAsync(id, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Cancelled
        });
        await store.CompleteAsync(id, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Succeeded
        });

        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await store.GetAsync(id))!.Status);
        AssertEx.Equal(expected: 0, (await store.ListLaunchReceiptsAsync()).Count);
    }

    [Test]
    public async Task Recover_FailsRunningRows_AndLeavesTheirReceiptsForTheReaper()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var running = Guid.NewGuid();
        var finished = Guid.NewGuid();
        await store.CreateRunningAsync(Create(running));
        await store.SetLaunchReceiptAsync(running, new byte[]
        {
            1
        });
        await store.CreateRunningAsync(Create(finished));
        await store.CompleteAsync(finished, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Succeeded
        });

        var recovered = await store.RecoverOnStartupAsync();

        AssertEx.Equal(running, recovered.Single());
        AssertEx.Equal(ModelLabExperimentStatus.Failed, (await store.GetAsync(running))!.Status);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await store.GetAsync(finished))!.Status);
        AssertEx.Equal(expected: 1, (await store.ListLaunchReceiptsAsync()).Count);
    }

    [Test]
    public async Task Trim_RemovesTheOldestFinishedRows_AndNeverARunningOne()
    {
        var clock = new SteppingClock();
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, clock);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await store.CreateRunningAsync(Create(id));
            if (i != 0)
            {
                await store.CompleteAsync(id, new ModelLabExperimentCompletion
                {
                    Status = ModelLabExperimentStatus.Succeeded
                });
            }
        }

        var removed = await store.TrimToNewestAsync(keep: 2);

        AssertEx.Equal(string.Join(',', ids.Skip(1).Take(2).OrderBy(static id => id)), string.Join(',', removed.OrderBy(static id => id)));
        AssertEx.Equal(ModelLabExperimentStatus.Running, (await store.GetAsync(ids[0]))!.Status, "The oldest row is running, so it stays.");
        var listed = await store.ListAsync();
        AssertEx.Equal(ids[4], listed[0].Id, "Newest first.");
    }

    [Test]
    public async Task Trim_LeavesAFinishedRowThatStillHoldsAReceipt()
    {
        var clock = new SteppingClock();
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, clock);
        var oldest = Guid.NewGuid();
        await store.CreateRunningAsync(Create(oldest));
        await store.CompleteAsync(oldest, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Failed
        });
        await store.SetLaunchReceiptAsync(oldest, new byte[]
        {
            1
        });
        var next = Guid.NewGuid();
        await store.CreateRunningAsync(Create(next));
        await store.CompleteAsync(next, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Failed
        });
        await store.CreateRunningAsync(Create(Guid.NewGuid()));

        var removed = await store.TrimToNewestAsync(keep: 1);

        AssertEx.Equal(next, removed.Single());
        AssertEx.Equal(expected: 1, (await store.ListLaunchReceiptsAsync()).Count, "The next startup still finds the orphan.");
    }

    [Test]
    public async Task Delete_RefusesARowThatStillHoldsAReceipt_UntilTheReaperClearsIt()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = Guid.NewGuid();
        await store.CreateRunningAsync(Create(id));
        await store.CompleteAsync(id, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Failed
        });
        await store.SetLaunchReceiptAsync(id, new byte[]
        {
            1
        });

        AssertEx.Equal(ModelLabExperimentDeleteOutcome.ProcessNotConfirmedStopped, await store.DeleteAsync(id));

        await store.SetLaunchReceiptAsync(id, launchReceiptJson: null);
        AssertEx.Equal(ModelLabExperimentDeleteOutcome.Deleted, await store.DeleteAsync(id));
        AssertEx.Equal(ModelLabExperimentDeleteOutcome.NotFound, await store.DeleteAsync(id));
    }

    private static ModelLabExperimentCreate Create(Guid id) =>
        new()
        {
            Id = id,
            RepoId = "Qwen/Qwen3.5-0.8B",
            Revision = "rev",
            BaseArtifactId = Guid.NewGuid(),
            InstalledModelName = "org/Some-GGUF:Q4_K_M",
            Prompt = Prompt,
            LicenseConfirmationJson = Encoding.UTF8.GetBytes("license-plaintext")
        };

    private static async Task<byte[]?> ReadRawAsync(NodeChatDbContext context, string column)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The column is one of this test's constants.
        command.CommandText = $"SELECT {column} FROM model_lab_experiments";
#pragma warning restore CA2100
        return await command.ExecuteScalarAsync() as byte[];
    }

    private async Task<NodeChatDbContext> CreateDatabaseAsync()
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, "lab.sqlite"), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        await context.Database.OpenConnectionAsync();
        return context;
    }

    private sealed class FixedKeyHolder : INodeSqliteKeyHolder
    {
        public ReadOnlyMemory<byte> Key { get; } = RandomNumberGenerator.GetBytes(32);

        public void Dispose()
        {
        }
    }

    /// <summary>Each read is later than the last, so creation order is the sort order.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
}
