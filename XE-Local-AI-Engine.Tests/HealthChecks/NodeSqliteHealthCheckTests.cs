namespace XE_Local_AI_Engine.Tests.HealthChecks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The node-local SQLite store is essential persistence: readiness must report Healthy only when the database is open,
///     readable, writable, and carries the node schema. An existing but read-only, or schema-incompatible, database must
///     flip /health/ready with a distinguishing reason even when the rest of the node is fine.
/// </summary>
public sealed class NodeSqliteHealthCheckTests
{
    [Test]
    public async Task OpenReadableWritableSchemaPresentDatabase_IsHealthy()
    {
        var dir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(dir, "node.db");
            var options = BuildOptions(dbPath);
            await CreateSchemaAsync(options).ConfigureAwait(false);

            using var keyHolder = new NullNodeSqliteKeyHolder();
            await using var dbContext = new NodeChatDbContext(options, keyHolder);

            var check = new NodeSqliteHealthCheck(dbContext);
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            AssertEx.Equal(HealthStatus.Healthy, result.Status);
            AssertEx.Equal("healthy", (string)result.Data["reason"]);
            AssertEx.True(result.Data.ContainsKey("probeMilliseconds"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task ExistingReadOnlyDatabase_IsUnhealthyWithWriteFailureReason()
    {
        var dir = CreateTempDir();
        var dbPath = Path.Combine(dir, "node.db");
        try
        {
            var options = BuildOptions(dbPath);
            await CreateSchemaAsync(options).ConfigureAwait(false);

            // Existing, schema-present, readable database that the process cannot write: the write-lock probe must fail
            // even though read and schema checks pass. Pooling is disabled so the probe opens a fresh handle that
            // reflects the read-only file mode rather than reusing a writable pooled connection.
            File.SetAttributes(dbPath, FileAttributes.ReadOnly);

            using var keyHolder = new NullNodeSqliteKeyHolder();
            await using var dbContext = new NodeChatDbContext(options, keyHolder);

            var check = new NodeSqliteHealthCheck(dbContext);
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            AssertEx.Equal(HealthStatus.Unhealthy, result.Status);
            AssertEx.Equal("unwritable", (string)result.Data["reason"]);
            // The description is a static string with NO interpolated provider message, so the
            // anonymous /health/ready payload can never leak internal error text. The structured reason still classifies it.
            AssertEx.Equal("Node SQLite database is not writable.", result.Description);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.SetAttributes(dbPath, FileAttributes.Normal);
            }

            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task ExistingDatabaseMissingSchema_IsUnhealthyWithSchemaReason()
    {
        var dir = CreateTempDir();
        try
        {
            // Intentionally skip CreateSchemaAsync: the probe opens an empty database that is readable and writable but
            // lacks the node schema (stands in for a replaced or schema-incompatible file).
            var options = BuildOptions(Path.Combine(dir, "node.db"));

            using var keyHolder = new NullNodeSqliteKeyHolder();
            await using var dbContext = new NodeChatDbContext(options, keyHolder);

            var check = new NodeSqliteHealthCheck(dbContext);
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            AssertEx.Equal(HealthStatus.Unhealthy, result.Status);
            AssertEx.Equal("schema-missing", (string)result.Data["reason"]);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Test]
    public async Task UnavailableDatabase_IsUnhealthyWithReason()
    {
        // A data source under a directory that does not exist cannot be opened, standing in for a dead store. The
        // provider's open failure carries the file path in its message, so this doubles as the leak guard below.
        var missingSegment = $"xe-missing-{Guid.NewGuid():N}";
        var unreachablePath = Path.Combine(Path.GetTempPath(), missingSegment, "node.db");
        var options = BuildOptions(unreachablePath);
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var dbContext = new NodeChatDbContext(options, keyHolder);

        var check = new NodeSqliteHealthCheck(dbContext);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        AssertEx.Equal(HealthStatus.Unhealthy, result.Status);
        AssertEx.Equal("unavailable", (string)result.Data["reason"]);
        // The description is a fixed string that never interpolates the provider message, so the
        // file path present in the underlying exception can never reach the anonymous /health/ready payload.
        AssertEx.Equal("Node SQLite database is unavailable.", result.Description);
        AssertEx.False(result.Description!.Contains(missingSegment, StringComparison.Ordinal),
            "the readiness description must not leak the database file path.");
    }

    private static DbContextOptions<NodeChatDbContext> BuildOptions(string dbPath)
    {
        return new DbContextOptionsBuilder<NodeChatDbContext>()
               // Pooling disabled so each probe opens a fresh file handle (the read-only test depends on the current file
               // mode rather than a reused pooled connection).
               .UseSqlite($"Data Source={dbPath};Pooling=False")
               // Mirror the production NodeChatDbContext registration: building distinct options per test would otherwise
               // push EF's internal service-provider cache over its cap once the whole module runs, and EF throws for that
               // event by default (full-suite-only failure).
               .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
               .Options;
    }

    private static async Task CreateSchemaAsync(DbContextOptions<NodeChatDbContext> options)
    {
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var dbContext = new NodeChatDbContext(options, keyHolder);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xe-sqlite-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory; a leaked temp file must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory; a leaked temp file must not fail the test.
        }
    }
}
