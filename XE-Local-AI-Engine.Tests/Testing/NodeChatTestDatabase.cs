namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>How a test fixture opens the node database file it owns.</summary>
internal static class NodeChatTestDatabase
{
    /// <summary>The connection string for a fixture-owned database file, with connection pooling turned off.</summary>
    /// <remarks>
    ///     Pooling is off because <c>SqliteConnectionPool.GetConnection</c> scans for "leaked" connections whenever
    ///     both pools are empty, which a concurrent fan-out guarantees, and <c>Activate</c> sets its active flag
    ///     before the weak reference — so a live connection reads as leaked and <c>Deactivate</c> resets it under its
    ///     owner, surfacing as SQLITE_BUSY well inside <c>busy_timeout</c>. Do not re-enable it.
    /// </remarks>
    public static string ConnectionString(string databasePath)
    {
        return $"Data Source={databasePath};Pooling=False";
    }
}
