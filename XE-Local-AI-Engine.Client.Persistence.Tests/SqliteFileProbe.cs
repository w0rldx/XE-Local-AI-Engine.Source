namespace XE_Local_AI_Engine.Client.Persistence.Tests;

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
    /// </summary>
    public static void ReleasePooledHandles() =>
        SqliteConnection.ClearAllPools();
}
