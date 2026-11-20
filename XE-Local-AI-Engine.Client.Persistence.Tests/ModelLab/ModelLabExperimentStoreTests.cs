namespace XE_Local_AI_Engine.Client.Persistence.Tests.ModelLab;

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task ACompletion_StoresTheAttentionBlobBesideTheTrace()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = Guid.NewGuid();
        var attention = Guid.NewGuid();
        await store.CreateRunningAsync(Create(id));

        await store.CompleteAsync(id, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Succeeded,
            TraceBlobId = Guid.NewGuid(),
            TraceSha256 = "trace",
            TraceByteCount = 10,
            AttentionBlobId = attention,
            AttentionSha256 = "attention",
            AttentionByteCount = 20
        });
        context.ChangeTracker.Clear();

        var read = (await store.GetAsync(id))!;
        AssertEx.Equal(attention, read.AttentionBlobId!.Value);
        AssertEx.Equal("attention", read.AttentionSha256!);
        AssertEx.Equal(expected: 20L, read.AttentionByteCount!.Value);
        AssertEx.Equal(expected: 10L, read.TraceByteCount!.Value);
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
    public async Task ANote_IsCiphertextAtRest_ReadsBackDecrypted_AndClears()
    {
        const string Note = "the operator's private note";
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = await CreateFinishedAsync(store);

        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Updated, await store.SetNotesAsync(id, Note));
        context.ChangeTracker.Clear();

        var raw = AssertEx.NotNull(await ReadRawAsync(context, "notes"));
        AssertEx.False(Encoding.UTF8.GetString(raw).Contains(Note, StringComparison.Ordinal), "notes is stored as plaintext.");
        AssertEx.Equal(Note, (await store.GetAsync(id))!.Notes!);

        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Updated, await store.SetNotesAsync(id, "  "));
        context.ChangeTracker.Clear();
        AssertEx.Null(await ReadRawAsync(context, "notes"));
        AssertEx.Null((await store.GetAsync(id))!.Notes);
    }

    [Test]
    public async Task ANote_WhenAadColumnSwapped_FailsToDecrypt()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = await CreateFinishedAsync(store, Encoding.UTF8.GetBytes("result-plaintext"));
        context.ChangeTracker.Clear();

        // Same key and row id: only the AAD column name differs, which is what must stop a result being presented as a note.
        var connection = context.Database.GetDbConnection();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE model_lab_experiments SET notes = result_json";
            AssertEx.Equal(expected: 1, await command.ExecuteNonQueryAsync());
        }

        _ = await AssertEx.ThrowsAsync<CryptographicException>(async () => _ = await store.GetAsync(id),
            "A ciphertext from another column must fail the AEAD tag check under the notes column's AAD.");
    }

    [Test]
    public async Task NotesAndPin_AreRefused_OnARunningRow_AndAnUnknownOne()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var running = Guid.NewGuid();
        await store.CreateRunningAsync(Create(running));

        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Running, await store.SetNotesAsync(running, "note"));
        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Running, await store.SetPinnedAsync(running, pinned: true, maxPinned: 20));
        AssertEx.Equal(ModelLabExperimentUpdateOutcome.NotFound, await store.SetNotesAsync(Guid.NewGuid(), "note"));
        AssertEx.Equal(ModelLabExperimentUpdateOutcome.NotFound, await store.SetPinnedAsync(Guid.NewGuid(), pinned: true, maxPinned: 20));
        context.ChangeTracker.Clear();
        var read = (await store.GetAsync(running))!;
        AssertEx.Null(read.Notes);
        AssertEx.False(read.IsPinned);
    }

    [Test]
    public async Task APin_PastTheCap_IsRefused_AndAnUnpinFreesAPlace()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var first = await CreateFinishedAsync(store);
        var second = await CreateFinishedAsync(store);
        var third = await CreateFinishedAsync(store);

        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Updated, await store.SetPinnedAsync(first, pinned: true, maxPinned: 2));
        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Updated, await store.SetPinnedAsync(second, pinned: true, maxPinned: 2));
        AssertEx.Equal(ModelLabExperimentUpdateOutcome.PinLimitReached, await store.SetPinnedAsync(third, pinned: true, maxPinned: 2));
        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Updated, await store.SetPinnedAsync(second, pinned: true, maxPinned: 2), "Re-pinning is not a new pin.");

        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Updated, await store.SetPinnedAsync(first, pinned: false, maxPinned: 2));
        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Updated, await store.SetPinnedAsync(third, pinned: true, maxPinned: 2));
        context.ChangeTracker.Clear();
        AssertEx.Equal(string.Join(',', new[]
            {
                second,
                third
            }.OrderBy(static id => id)),
            string.Join(',', (await store.ListAsync()).Where(static row => row.IsPinned).Select(static row => row.Id).OrderBy(static id => id)));
    }

    [Test]
    public async Task Trim_KeepsEveryPinnedRow_AndTheNewestUnpinnedOnes()
    {
        var clock = new SteppingClock();
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, clock);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await CreateFinishedAsync(store));
        }

        // The two oldest are pinned, so the newest two of the other three are kept and only the middle one goes.
        _ = await store.SetPinnedAsync(ids[0], pinned: true, maxPinned: 20);
        _ = await store.SetPinnedAsync(ids[1], pinned: true, maxPinned: 20);

        var removed = await store.TrimToNewestAsync(keep: 2);

        AssertEx.Equal(ids[2], removed.Single());
        AssertEx.Equal(expected: 4, (await store.ListAsync()).Count);
    }

    [Test]
    public async Task TheAppVersion_IsStoredWithTheRow_AndAnOldRowHasNone()
    {
        await using var context = await CreateDatabaseAsync();
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var recorded = Guid.NewGuid();
        await store.CreateRunningAsync(new ModelLabExperimentCreate
        {
            Id = recorded,
            RepoId = "Qwen/Qwen3.5-0.8B",
            Revision = "rev",
            BaseArtifactId = Guid.NewGuid(),
            InstalledModelName = "org/Some-GGUF:Q4_K_M",
            Prompt = Prompt,
            LicenseConfirmationJson = Encoding.UTF8.GetBytes("license-plaintext"),
            AppVersion = "1.0.0-rc.3+abc"
        });
        var old = await CreateFinishedAsync(store);

        AssertEx.Equal("1.0.0-rc.3+abc", (await store.GetAsync(recorded))!.AppVersion!);
        AssertEx.Null((await store.GetAsync(old))!.AppVersion);
    }

    [Test]
    public async Task Trim_KeepsARowPinnedAfterItsSelect_AndDoesNotReportIt()
    {
        var pinLate = new PinBeforeFirstDelete();
        await using var context = await CreateDatabaseAsync(pinLate);
        var store = new ModelLabExperimentStore(context, new SteppingClock());
        for (var i = 0; i < 4; i++)
        {
            _ = await CreateFinishedAsync(store);
        }

        var removed = await store.TrimToNewestAsync(keep: 1);

        AssertEx.Equal(expected: 1, pinLate.PinnedRows, "The first candidate was pinned between trim's select and its delete.");
        var rows = await store.ListAsync();
        var pinned = rows.Single(static row => row.IsPinned);
        AssertEx.False(removed.Contains(pinned.Id), "A row pinned after the select is not reported, so its blobs stay.");
        AssertEx.Equal(expected: 2, removed.Count, "The other two candidates still go.");
        AssertEx.Equal(expected: 2, rows.Count, "The newest row and the pinned one remain.");
    }

    [Test]
    public async Task Trim_ThatFailsPartWay_DeletesNothing()
    {
        var failSecond = new FailSecondDelete();
        await using var context = await CreateDatabaseAsync(failSecond);
        var store = new ModelLabExperimentStore(context, new SteppingClock());
        for (var i = 0; i < 3; i++)
        {
            _ = await CreateFinishedAsync(store);
        }

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => store.TrimToNewestAsync(keep: 1));

        AssertEx.Equal(expected: 2, failSecond.Deletes, "The first delete ran before the second failed.");
        AssertEx.Equal(expected: 3, (await store.ListAsync()).Count, "The first delete was rolled back, so no row is left without its blobs' cleanup.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnUpdate_OnARowDeletedAfterItsLoad_IsNotFound(bool pin)
    {
        var deleteLate = new DeleteBeforeNextSave();
        await using var context = await CreateDatabaseAsync(deleteLate);
        var store = new ModelLabExperimentStore(context, TimeProvider.System);
        var id = await CreateFinishedAsync(store);
        context.ChangeTracker.Clear();
        deleteLate.Armed = true;

        var outcome = pin
            ? await store.SetPinnedAsync(id, pinned: true, maxPinned: 20)
            : await store.SetNotesAsync(id, "a note");

        AssertEx.Equal(ModelLabExperimentUpdateOutcome.NotFound, outcome);
        AssertEx.False(deleteLate.Armed, "The row was deleted between the load and the save.");
        AssertEx.Null(await store.GetAsync(id));
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

    private static async Task<Guid> CreateFinishedAsync(ModelLabExperimentStore store, byte[]? resultJson = null)
    {
        var id = Guid.NewGuid();
        await store.CreateRunningAsync(Create(id));
        await store.CompleteAsync(id, new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Succeeded,
            // A bare null byte[] would convert to an empty memory, not to "no result".
            ResultJson = resultJson is null ? null : (ReadOnlyMemory<byte>?)resultJson
        });
        return id;
    }

    private static async Task<byte[]?> ReadRawAsync(NodeChatDbContext context, string column)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The column is one of this test's constants.
        command.CommandText = $"SELECT {column} FROM model_lab_experiments";
#pragma warning restore CA2100
        return await command.ExecuteScalarAsync() as byte[];
    }

    private async Task<NodeChatDbContext> CreateDatabaseAsync(IInterceptor? extraInterceptor = null)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "lab.sqlite");
        var context = extraInterceptor is null
            ? AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder)
            : AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder, extraInterceptor);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        await context.Database.OpenConnectionAsync();
        return context;
    }

    /// <summary>Pins the row trim's first delete names, on the same connection just before that delete runs: a pin that lands after trim's select.</summary>
    private sealed class PinBeforeFirstDelete : DbCommandInterceptor
    {
        public int PinnedRows { get; private set; } = -1;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (PinnedRows < 0 && command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                var target = (SqliteParameter)command.Parameters.Cast<DbParameter>().Single();
                await using var pin = ((SqliteConnection)command.Connection!).CreateCommand();
                pin.Transaction = (SqliteTransaction?)command.Transaction;
                pin.CommandText = "UPDATE model_lab_experiments SET is_pinned = 1 WHERE id = $id";
                _ = pin.Parameters.Add(new SqliteParameter("$id", target.SqliteType)
                {
                    Value = target.Value
                });
                PinnedRows = await pin.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Lets trim's first delete run and fails its second, as a locked or full database would.</summary>
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

    /// <summary>Once armed, deletes every row just before the next save: a trim or a manual delete landing between an update's load and its save.</summary>
    private sealed class DeleteBeforeNextSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);
            if (Armed)
            {
                Armed = false;
                await using var delete = eventData.Context!.Database.GetDbConnection().CreateCommand();
                delete.CommandText = "DELETE FROM model_lab_experiments";
                AssertEx.Equal(expected: 1, await delete.ExecuteNonQueryAsync(cancellationToken));
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
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
