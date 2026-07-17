namespace XE_Local_AI_Engine.Tests.Persistence;

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     AUD4-08 instrumentation: classifies SQLITE_BUSY / SQLITE_LOCKED failures and records them on the
///     <c>sqlite_busy_total</c> counter with bounded dimensions. A real busy is provoked with two connections so the
///     classifier is exercised against an actual <see cref="SqliteException" />, not a synthetic one.
/// </summary>
[NotInParallel]
public sealed class NodeSqliteContentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public NodeSqliteContentionTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Test]
    public void Classify_ReturnsNull_ForUnrelatedOrNullException()
    {
        AssertEx.Null(NodeSqliteContention.Classify(new InvalidOperationException("unrelated")));
        AssertEx.Null(NodeSqliteContention.Classify(exception: null));
    }

    [Test]
    public void Record_IsNoOp_ForNonContentionException()
    {
        using var capture = new BusyMeterCapture();

        NodeSqliteContention.Record("raw", new InvalidOperationException("unrelated"));

        AssertEx.Equal(expected: 0, capture.TotalCount);
    }

    [Test]
    public async Task Record_ClassifiesRealSqliteBusy_AndIncrementsCounterWithCodeAndPathTags()
    {
        var busy = await ProvokeBusyAsync();
        AssertEx.Equal("busy", NodeSqliteContention.Classify(busy));

        using var capture = new BusyMeterCapture();
        NodeSqliteContention.Record("raw", busy);

        var records = capture.Records;
        AssertEx.Equal(expected: 1, records.Count);
        AssertEx.Equal("busy", records[0].Code);
        AssertEx.Equal("raw", records[0].Path);
        AssertEx.Equal(expected: 1L, records[0].Value);
    }

    // Provokes a genuine SQLITE_BUSY: a holder connection takes the WAL write lock, then a second connection whose
    // busy_timeout is 0 (and whose command retry budget is the 1s minimum) attempts a write and is refused.
    private async Task<SqliteException> ProvokeBusyAsync()
    {
        var path = Path.Combine(_dir, "busy.sqlite");

        await using var holder = new SqliteConnection($"Data Source={path}");
        await holder.OpenAsync();
        await NodeSqlitePragmas.ApplyAsync(holder, new NodeSqlitePragmaSettings(EnableWriteAheadLog: true, BusyTimeoutMilliseconds: 5000, NodeSqliteSynchronousMode.Normal), logger: null,
            CancellationToken.None);
        await ExecuteAsync(holder, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);");

        await using var writer = new SqliteConnection($"Data Source={path}");
        await writer.OpenAsync();
        await NodeSqlitePragmas.ApplyAsync(writer, new NodeSqlitePragmaSettings(EnableWriteAheadLog: true, BusyTimeoutMilliseconds: 0, NodeSqliteSynchronousMode.Normal), logger: null,
            CancellationToken.None);

        await using var holderTransaction = (SqliteTransaction)await holder.BeginTransactionAsync(CancellationToken.None);
        await ExecuteAsync(holder, "INSERT INTO t(v) VALUES('holder');", holderTransaction);

        SqliteException? caught = null;
        await using (var command = writer.CreateCommand())
        {
            command.CommandText = "INSERT INTO t(v) VALUES('waiter');";
            command.CommandTimeout = 1; // minimum retry budget; with busy_timeout=0 the write is refused after ~1s
            try
            {
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (SqliteException exception)
            {
                caught = exception;
            }
        }

        await holderTransaction.CommitAsync(CancellationToken.None);

        return caught ?? throw new InvalidOperationException("Expected SQLITE_BUSY was not raised by the contending writer.");
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only fixed SQL text — never user input.")]
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class BusyMeterCapture : IDisposable
    {
        private readonly List<(string Code, string Path, long Value)> _records = [];
        private readonly MeterListener _listener = new();
        private readonly Lock _sync = new();

        public BusyMeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Name, "sqlite_busy_total", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.Start();
        }

        public int TotalCount
        {
            get
            {
                lock (_sync)
                {
                    return _records.Count;
                }
            }
        }

        public IReadOnlyList<(string Code, string Path, long Value)> Records
        {
            get
            {
                lock (_sync)
                {
                    return [.. _records];
                }
            }
        }

        public void Dispose()
        {
            _listener.Dispose();
        }

        private void OnMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var code = string.Empty;
            var path = string.Empty;
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "code", StringComparison.Ordinal))
                {
                    code = tag.Value as string ?? string.Empty;
                }
                else if (string.Equals(tag.Key, "path", StringComparison.Ordinal))
                {
                    path = tag.Value as string ?? string.Empty;
                }
            }

            lock (_sync)
            {
                _records.Add((code, path, measurement));
            }
        }
    }
}
