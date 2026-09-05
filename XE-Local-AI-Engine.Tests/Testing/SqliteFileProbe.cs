namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Data.Sqlite;

/// <summary>
///     Reads a SQLite database file after releasing every pooled connection to it.
///     <para>
///         Microsoft.Data.Sqlite pools connections: disposing the <c>DbContext</c> returns the connection to the pool,
///         and the pool keeps the underlying file handle open. POSIX permits a second reader regardless, so the
///         at-rest-encryption assertions that read the raw database file passed on Linux for years. Windows denies the
///         share, and every one of them failed with "The process cannot access the file … because it is being used by
///         another process" the first time this suite ran there.
///     </para>
///     <para>
///         Clearing the pool before reading is what makes those assertions portable. It is not a test-only nicety: an
///         assertion that cannot open the file proves nothing about what the file contains.
///     </para>
/// </summary>
internal static class SqliteFileProbe
{
    /// <summary>Releases pooled handles, then reads the database file's raw bytes.</summary>
    public static async Task<byte[]> ReadAllBytesAsync(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        return await File.ReadAllBytesAsync(databasePath).ConfigureAwait(false);
    }

    /// <summary>
    ///     Releases pooled handles so a fixture can delete its database directory. Windows refuses to remove a
    ///     directory containing an open file; Linux happily unlinks it, which is why teardown never failed there.
    ///     <para>
    ///         Which is also why the call is guarded. <see cref="SqliteConnection.ClearAllPools" /> is process-global:
    ///         it closes every pooled connection in the test host, not just this fixture's. Seven
    ///         <c>DevelopmentTestFixture</c> siblings call this from <c>Dispose()</c>, and TUnit runs classes in
    ///         parallel, so on Linux one class's teardown was tearing down a sibling's still-open connection to its own
    ///         database for no benefit at all — the directory delete would have succeeded either way. That is the flake
    ///         <c>DevelopmentReworkEdgeTests</c> carries a <c>[NotInParallel]</c> to dodge. On Windows the call is
    ///         still what makes teardown work, so it stays there.
    ///     </para>
    ///     <para>
    ///         Deliberately NOT applied to <see cref="ReadAllBytesAsync" />: closing the pool also checkpoints WAL back
    ///         into the main database file, and the at-rest assertions scan that file. Skipping it on Linux would leave
    ///         a WAL-mode host's plaintext in a sidecar the scan never reads, turning those assertions green for the
    ///         wrong reason.
    ///     </para>
    /// </summary>
    public static void ReleasePooledHandles()
    {
        if (OperatingSystem.IsWindows())
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
