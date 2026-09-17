namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Guards the SQLite build that <c>SQLitePCLRaw.lib.e_sqlite3</c> actually loads at run time.
///     <para>
///         CVE-2025-6965 (GHSA-2m69-gcr7-jv3q, High) is fixed in SQLite 3.50.2. The repo used to carry a
///         <c>Directory.Packages.props</c> override pinning the native package to a 3.5x.x build because the
///         SQLitePCLRaw 2.1.x bundle shipped an older SQLite; the bundle now carries the fixed build itself, so the
///         override is gone. Nothing in the package graph states the native SQLite version — only the loaded library
///         does — so a bundle regression or a deliberate downgrade would be invisible without this assertion.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NativeSqliteVersionTests
{
    /// <summary>The release that fixed CVE-2025-6965; the loaded native library must never be older.</summary>
    private static readonly Version MinimumPatchedVersion = new(3, 50, 2);

    [Test]
    public async Task SqliteVersion_OnTheLoadedNativeLibrary_IsAtLeastTheCveFix()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "select sqlite_version()";
        var reported = (string?)await command.ExecuteScalarAsync();

        var actual = Version.Parse(AssertEx.NotNull(reported, "sqlite_version() should report a version string."));
        AssertEx.True(
            actual >= MinimumPatchedVersion,
            $"The loaded native SQLite is {actual}; CVE-2025-6965 requires at least {MinimumPatchedVersion}.");
    }
}
