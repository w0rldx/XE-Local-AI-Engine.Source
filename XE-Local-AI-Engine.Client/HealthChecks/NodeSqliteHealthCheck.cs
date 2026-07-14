namespace XE_Local_AI_Engine.Client.HealthChecks;

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Readiness probe for the node-local SQLite database. Persistence is essential: a node that cannot open or read its
///     own SQLite store cannot serve chat, agents, or scheduling, so readiness must flip when the database is dead or
///     unwritable. The probe reuses the app's own <see cref="NodeChatDbContext" /> (resolved per health-check scope) so the
///     connection string and encryption posture are identical to production reads — it never opens a raw connection with
///     its own key plumbing. The query is a bounded, non-mutating <c>SELECT 1</c>.
/// </summary>
public sealed class NodeSqliteHealthCheck : IHealthCheck
{
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
                var connection = _dbContext.Database.GetDbConnection();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                var scalar = await command.ExecuteScalarAsync(probeToken).ConfigureAwait(false);

                stopwatch.Stop();
                var data = BuildData(stopwatch);
                if (scalar is null)
                {
                    return HealthCheckResult.Unhealthy("Node SQLite probe returned no result.", data: data);
                }

                return HealthCheckResult.Healthy("Node SQLite database is open and readable.", data);
            }
            finally
            {
                await _dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return HealthCheckResult.Unhealthy(
                $"Node SQLite probe timed out after {ProbeTimeout.TotalSeconds:0.#}s.",
                data: BuildData(stopwatch));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return HealthCheckResult.Unhealthy(
                $"Node SQLite database is unavailable: {ex.Message}",
                exception: ex,
                data: BuildData(stopwatch));
        }
    }

    private static IReadOnlyDictionary<string, object> BuildData(Stopwatch stopwatch)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["probeMilliseconds"] = stopwatch.Elapsed.TotalMilliseconds
        };
    }
}
