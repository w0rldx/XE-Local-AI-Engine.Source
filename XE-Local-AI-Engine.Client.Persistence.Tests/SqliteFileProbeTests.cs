namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Pins both halves of <see cref="SqliteFileProbe.ReadAllBytesAsync" />: the probed database's rows must reach the
///     main file the at-rest scans read, and no other database's pooled handles may be touched.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class SqliteFileProbeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sqlite-file-probe-{Guid.NewGuid():N}");

    public SqliteFileProbeTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.sqlite"))
        {
            using var poolKey = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(poolKey);
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a lingering handle must not fail the test.
        }
    }

    [Test]
    public async Task ReadAllBytesAsync_FoldsTheWalIntoTheFileItReads()
    {
        var probed = await SeedAsync("probed", "probed-marker");
        AssertEx.True(File.Exists(probed + "-wal"), "A pooled WAL-mode connection should leave the write in the sidecar.");

        var bytes = await SqliteFileProbe.ReadAllBytesAsync(probed);

        AssertEx.False(File.Exists(probed + "-wal"), "The probe must checkpoint the WAL away before reading the file.");
        AssertEx.True(Encoding.UTF8.GetString(bytes).Contains("probed-marker", StringComparison.Ordinal),
            "The row has to be readable in the main file the at-rest scan reads.");
    }

    [Test]
    public async Task ReadAllBytesAsync_LeavesAnotherDatabasesPooledHandleAlone()
    {
        var sibling = await SeedAsync("sibling", "sibling-marker");
        var probed = await SeedAsync("probed", "probed-marker");
        AssertEx.True(File.Exists(sibling + "-wal"), "The sibling's write should still be sitting in its own WAL.");

        _ = await SqliteFileProbe.ReadAllBytesAsync(probed);

        AssertEx.True(File.Exists(sibling + "-wal"),
            "Probing one database must not close a parallel sibling's pooled connection to a different database.");
    }

    [Test]
    public async Task ReadAllBytesAsync_DoesNotDisturbAnOpenTransactionOnAnotherDatabase()
    {
        var busy = await SeedAsync("busy", "seed");
        var probed = await SeedAsync("probed", "probed-marker");

        await using var connection = new SqliteConnection($"Data Source={busy}");
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await InsertMarkAsync(connection, transaction, "in-flight");

        _ = await Task.Run(() => SqliteFileProbe.ReadAllBytesAsync(probed));

        await transaction.CommitAsync();
        AssertEx.Equal(expected: 2L, await CountMarksAsync(busy), "The transaction held open across the probe must still commit.");
    }

    private async Task<string> SeedAsync(string name, string marker)
    {
        var path = Path.Combine(_directory, $"{name}.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;");
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS marks(value TEXT NOT NULL);");
            await InsertMarkAsync(connection, transaction: null, marker);
        }

        return path;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed literals from this suite; every value is a bound parameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertMarkAsync(SqliteConnection connection, SqliteTransaction? transaction, string marker)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO marks(value) VALUES ($value);";
        _ = command.Parameters.AddWithValue("$value", marker);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountMarksAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM marks;";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
