namespace XE_Local_AI_Engine.Client.HealthChecks;

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Readiness probe for the node-local SQLite database. Persistence is essential: a node that cannot open, read,
///     write, or that is missing its schema cannot serve chat, agents, or scheduling, so readiness must flip in any of
///     those cases. The probe reuses the app's own <see cref="NodeChatDbContext" /> (resolved per health-check scope) so
///     the connection string and encryption posture are identical to production access — it never opens a raw connection
///     with its own key plumbing. Within a single bounded window it exercises three capabilities without persistent
///     domain mutation:
///     <list type="number">
///         <item><description>read: <c>SELECT 1</c> proves the file is open and readable;</description></item>
///         <item>
///             <description>
///                 schema: a sentinel core table is present in <c>sqlite_master</c> (guards a replaced or
///                 schema-incompatible database that opens but lacks the node schema);
///             </description>
///         </item>
///         <item>
///             <description>
///                 write: inside a <c>BEGIN IMMEDIATE</c> transaction, a scratch-table DDL forces an actual write to the
///                 main database (which fails on a read-only or otherwise unwritable file), then rolls back — so the
///                 write path is proven with zero net mutation.
///             </description>
///         </item>
///     </list>
///     Failure reasons are distinguished in the description and the <c>reason</c> data entry.
/// </summary>
public sealed class NodeSqliteHealthCheck : IHealthCheck
{
    // A representative core table: present whenever the node schema exists (created by migrations in production and by
    // EnsureCreated in tests), absent on a blank or replaced database. Its presence is a cheap schema-equivalence
    // sentinel; a wholesale schema diff is intentionally not performed.
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
            await _dbContext.Database.OpenConnectionAsync(probeToken).ConfigureAwait(false);
            try
            {
                var connection = (SqliteConnection)_dbContext.Database.GetDbConnection();

                // 1. Readable.
                await using (var readCommand = connection.CreateCommand())
                {
                    readCommand.CommandText = "SELECT 1;";
                    var readResult = await readCommand.ExecuteScalarAsync(probeToken).ConfigureAwait(false);
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
                    var schemaResult = await schemaCommand.ExecuteScalarAsync(probeToken).ConfigureAwait(false);
                    if (schemaResult is null)
                    {
                        return Unhealthy(stopwatch, reason: "schema-missing",
                            $"Node SQLite schema is incomplete: expected table '{SchemaSentinelTable}' is missing.");
                    }
                }

                // 3. Writable — BEGIN IMMEDIATE alone only takes an advisory reserved lock and does not touch the file, so
                // it succeeds even on a read-only database. A DDL write inside the transaction forces an actual page write
                // to the main database (which fails with "attempt to write a readonly database" when the file is not
                // writable); the rollback undoes the scratch table, so there is zero net mutation on a writable database.
                try
                {
                    await using (var beginCommand = connection.CreateCommand())
                    {
                        beginCommand.CommandText = "BEGIN IMMEDIATE;";
                        _ = await beginCommand.ExecuteNonQueryAsync(probeToken).ConfigureAwait(false);
                    }

                    await using (var writeCommand = connection.CreateCommand())
                    {
                        writeCommand.CommandText = "CREATE TABLE _xe_write_probe (probe INTEGER);";
                        _ = await writeCommand.ExecuteNonQueryAsync(probeToken).ConfigureAwait(false);
                    }

                    await using var rollbackCommand = connection.CreateCommand();
                    rollbackCommand.CommandText = "ROLLBACK;";
                    _ = await rollbackCommand.ExecuteNonQueryAsync(probeToken).ConfigureAwait(false);
                }
                catch (Exception writeException) when (writeException is not OperationCanceledException)
                {
                    // The write probe left an open transaction if BEGIN succeeded but the DDL failed; closing the
                    // connection below rolls it back, so the scratch table is never persisted either way. The raw
                    // provider message is NOT interpolated into the description: /health/ready is anonymous and, on a
                    // non-loopback/proxied deployment, would otherwise leak internal error text (including filesystem
                    // paths) to remote callers. The structured "unwritable" reason and the
                    // exception (for server-side health logging) are preserved.
                    return Unhealthy(stopwatch, reason: "unwritable",
                        "Node SQLite database is not writable.", writeException);
                }

                stopwatch.Stop();
                return HealthCheckResult.Healthy("Node SQLite database is open, readable, writable, and schema-present.",
                    BuildData(stopwatch, reason: "healthy"));
            }
            finally
            {
                await _dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
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
