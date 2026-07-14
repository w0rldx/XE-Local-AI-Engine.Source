namespace XE_Local_AI_Engine.Tests.HealthChecks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The node-local SQLite store is essential persistence: readiness must report Healthy when the database is open and
///     readable, and Unhealthy (with a reason) when it cannot be opened — a dead/unwritable database must flip
///     /health/ready even when the rest of the node is fine.
/// </summary>
public sealed class NodeSqliteHealthCheckTests
{
    [Test]
    public async Task OpenReadableDatabase_IsHealthy()
    {
        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
            .UseSqlite("Data Source=:memory:")
            // Mirror the production NodeChatDbContext registration: building distinct options per test would otherwise
            // push EF's internal service-provider cache over its cap once the whole module runs, and EF throws for that
            // event by default (full-suite-only failure).
            .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var dbContext = new NodeChatDbContext(options, keyHolder);

        var check = new NodeSqliteHealthCheck(dbContext);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        AssertEx.Equal(HealthStatus.Healthy, result.Status);
        AssertEx.True(result.Data.ContainsKey("probeMilliseconds"));
    }

    [Test]
    public async Task UnavailableDatabase_IsUnhealthyWithReason()
    {
        // A data source under a directory that does not exist cannot be opened, standing in for a dead/unwritable store.
        var unreachablePath = Path.Combine(Path.GetTempPath(), $"xe-missing-{Guid.NewGuid():N}", "node.db");
        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
            .UseSqlite($"Data Source={unreachablePath}")
            .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var dbContext = new NodeChatDbContext(options, keyHolder);

        var check = new NodeSqliteHealthCheck(dbContext);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        AssertEx.Equal(HealthStatus.Unhealthy, result.Status);
        AssertEx.NotNull(result.Description);
        AssertEx.True(result.Description!.Contains("unavailable", StringComparison.Ordinal));
    }
}
