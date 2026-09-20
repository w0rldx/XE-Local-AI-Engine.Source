namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>Readiness probe for the node-local SQLite database.</summary>
/// <remarks>
///     A node that cannot open, read or write its database, or that is missing its schema, cannot serve chat, agents or scheduling, so readiness must
///     flip in any of those cases. <see cref="NodeChatDbContext" /> is reused, resolved per health-check scope, so the connection string and encryption
///     posture are identical to production access — the probe never opens a raw connection with its own key plumbing. Failure reasons are distinguished
///     in the description and the <c>reason</c> data entry. What the three probe steps are and why each is needed:
///     docs/wiki/08-data-and-persistence.md ("Readiness: what the SQLite probe proves").
/// </remarks>
public sealed class NodeSqliteHealthCheck : IHealthCheck
{
    // A representative core table: present whenever the node schema exists (created by migrations in production and by EnsureCreated in tests), absent
    // on a blank or replaced database. Its presence is a cheap schema-equivalence sentinel; a wholesale schema diff is intentionally not performed.
    private const string SchemaSentinelTable = "conversations";

    // A readiness probe must be fast: a hung or contended database should surface as unhealthy quickly rather than
    // stalling the /health/ready poll. This bounds the open+probe round-trip independently of the caller's token.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly NodeChatDbContext _dbContext;

    public NodeSqliteHealthCheck(NodeChatDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);
        var probeToken = timeoutCts.Token;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _dbContext.Database.OpenConnectionAsync(probeToken);
            var connection = (SqliteConnection)_dbContext.Database.GetDbConnection();

            // The write probe's transaction must be rolled back on EVERY exit (DDL failure, the 2s probe timeout, caller cancellation), because
            // Microsoft.Data.Sqlite pools handles: "closing" returns one SQLite still thinks is mid-transaction, and the next consumer fails to begin one.
            var transactionOpen = false;
            try
            {
                // 1. Readable.
                await using (var readCommand = connection.CreateCommand())
                {
                    readCommand.CommandText = "SELECT 1;";
                    var readResult = await readCommand.ExecuteScalarAsync(probeToken);
                    if (readResult is null)
                    {
                        return Unhealthy(stopwatch, reason: "unavailable", "Node SQLite probe returned no result.");
                    }
                }

                // 2. Schema present (parameterised sentinel-table lookup; the table name is a compile-time constant).
                await using (var schemaCommand = connection.CreateCommand())
                {
                    schemaCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1;";
                    _ = schemaCommand.Parameters.AddWithValue("$table", SchemaSentinelTable);
                    var schemaResult = await schemaCommand.ExecuteScalarAsync(probeToken);
                    if (schemaResult is null)
                    {
                        return Unhealthy(stopwatch, reason: "schema-missing",
                            $"Node SQLite schema is incomplete: expected table '{SchemaSentinelTable}' is missing.");
                    }
                }

                // 3. Writable — BEGIN IMMEDIATE alone takes only an advisory reserved lock and never touches the file, so it succeeds even on a read-only database.
                // A DDL write inside the transaction forces a real page write, failing with "attempt to write a readonly database"; the rollback leaves zero net mutation.
                try
                {
                    await using (var beginCommand = connection.CreateCommand())
                    {
                        beginCommand.CommandText = "BEGIN IMMEDIATE;";
                        _ = await beginCommand.ExecuteNonQueryAsync(probeToken);
                        transactionOpen = true;
                    }

                    await using (var writeCommand = connection.CreateCommand())
                    {
                        writeCommand.CommandText = "CREATE TABLE _xe_write_probe (probe INTEGER);";
                        _ = await writeCommand.ExecuteNonQueryAsync(probeToken);
                    }

                    await using var rollbackCommand = connection.CreateCommand();
                    rollbackCommand.CommandText = "ROLLBACK;";
                    _ = await rollbackCommand.ExecuteNonQueryAsync(probeToken);
                    transactionOpen = false;
                }
                catch (Exception writeException) when (writeException is not OperationCanceledException)
                {
                    // The transaction BEGIN opened is rolled back by the finally below, and the raw provider message is NOT interpolated into the description:
                    // /health/ready is anonymous, so on a proxied deployment it would leak internal error text (filesystem paths included) to remote callers.
                    return Unhealthy(stopwatch, reason: "unwritable",
                        "Node SQLite database is not writable.", writeException);
                }

                stopwatch.Stop();
                return HealthCheckResult.Healthy("Node SQLite database is open, readable, writable, and schema-present.",
                    BuildData(stopwatch, reason: "healthy"));
            }
            finally
            {
                if (transactionOpen)
                {
                    try
                    {
                        // No token: a rollback must still run when the probe token is what expired. A rollback failure
                        // must never mask the original error either, hence the best-effort catch.
                        await using var rollbackCommand = connection.CreateCommand();
                        rollbackCommand.CommandText = "ROLLBACK;";
                        _ = await rollbackCommand.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                    catch (Exception)
                    {
                        // Best effort: the connection is already gone, or the transaction is no longer open.
                    }
                }

                await _dbContext.Database.CloseConnectionAsync();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unhealthy(stopwatch, reason: "timeout",
                $"Node SQLite probe timed out after {ProbeTimeout.TotalSeconds:0.#}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Static description only — the raw provider message (which can carry the database file path) must never
            // reach the anonymous /health/ready payload. The reason code and exception are kept.
            return Unhealthy(stopwatch, reason: "unavailable", "Node SQLite database is unavailable.", ex);
        }
    }

    private static HealthCheckResult Unhealthy(Stopwatch stopwatch, string reason, string description, Exception? exception = null)
    {
        stopwatch.Stop();
        return HealthCheckResult.Unhealthy(description, exception, BuildData(stopwatch, reason));
    }

    private static IReadOnlyDictionary<string, object> BuildData(Stopwatch stopwatch, string reason)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["probeMilliseconds"] = stopwatch.Elapsed.TotalMilliseconds
        };
    }
}
