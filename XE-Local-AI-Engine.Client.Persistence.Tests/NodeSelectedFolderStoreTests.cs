namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class NodeSelectedFolderStoreTests : IDisposable
{
    private const string HostPath = "/trusted/host/projects/repo-one";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddAsync_ThenReadBackInNewContext_DecryptsHostPath()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid folderId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new NodeSelectedFolderStore(writeContext, TimeProvider.System);
            var added = await store.AddAsync("repo-one", HostPath, SelectedFolderMode.Copy);

            AssertEx.Equal("repo-one", added.Alias);
            AssertEx.Equal(HostPath, added.HostPath);
            AssertEx.Equal(SelectedFolderMode.Copy, added.Mode);
            AssertEx.True(added.Id != Guid.Empty, "Add should assign a folder id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp a creation time.");
            folderId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new NodeSelectedFolderStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(folderId), "Folder should be found by id.");
        AssertEx.Equal(HostPath, byId.HostPath);

        var byAlias = AssertEx.NotNull(await readStore.GetByAliasAsync("repo-one"), "Folder should be found by alias.");
        AssertEx.Equal(folderId, byAlias.Id);

        var unknown = await readStore.GetByIdAsync(Guid.NewGuid());
        AssertEx.Null(unknown, "Unknown id should return null.");
    }

    [Test]
    public async Task AddAsync_WithReadOnlyMount_RoundTripsNonDefaultMode()
    {
        var databasePath = GetDatabasePath("mode.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid folderId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new NodeSelectedFolderStore(context, TimeProvider.System);
            var added = await store.AddAsync("mount-one", HostPath, SelectedFolderMode.ReadOnlyMount);
            folderId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new NodeSelectedFolderStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(folderId), "Folder should be found by id.");
        AssertEx.Equal(SelectedFolderMode.ReadOnlyMount, record.Mode);
    }

    [Test]
    public async Task DatabaseFile_AfterAdd_DoesNotContainPlaintextHostPath()
    {
        var databasePath = GetDatabasePath("ciphertext.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var hostPath = "/secret/host/path/" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new NodeSelectedFolderStore(context, TimeProvider.System);
            _ = await store.AddAsync("secret-folder", hostPath, SelectedFolderMode.Copy);
        }

        SqliteFileProbe.ReleasePooledHandles();
        var databaseFamily = Directory.EnumerateFiles(Path.GetDirectoryName(databasePath)!, Path.GetFileName(databasePath) + "*").ToArray();
        AssertEx.True(databaseFamily.Length > 0, "The plaintext probe must inspect at least the SQLite database file.");
        foreach (var file in databaseFamily)
        {
            var fileBytes = await File.ReadAllBytesAsync(file);
            AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(hostPath)),
                $"The SQLite database family member '{Path.GetFileName(file)}' should not contain the plaintext host path.");
        }
    }

    [Test]
    public async Task AddAsync_WithDuplicateAlias_ThrowsOnUniqueIndex()
    {
        var databasePath = GetDatabasePath("dupe.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new NodeSelectedFolderStore(context, TimeProvider.System);

        _ = await store.AddAsync("repo-one", "/trusted/a", SelectedFolderMode.Copy);

        _ = AssertEx.Throws<DbUpdateException>(() => store.AddAsync("repo-one", "/trusted/b", SelectedFolderMode.Copy).GetAwaiter().GetResult(),
            "Duplicate alias should violate the unique index.");
    }

    [Test]
    public async Task RevokeAsync_HidesFolderFromEveryActiveRead_AndIsIdempotent()
    {
        var databasePath = GetDatabasePath("revoke.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new NodeSelectedFolderStore(context, TimeProvider.System);
        var added = await store.AddAsync("repo-one", HostPath, SelectedFolderMode.ReadOnlyMount);

        AssertEx.True(await store.RevokeAsync(added.Id), "The first revocation should update the active row.");
        AssertEx.False(await store.RevokeAsync(added.Id), "A repeated revocation should be an idempotent no-op.");
        AssertEx.Null(await store.GetByIdAsync(added.Id), "A revoked id must be indistinguishable from an unknown id.");
        AssertEx.Null(await store.GetByAliasAsync(added.Alias), "A revoked alias must not resolve.");
        AssertEx.Equal(expected: 0, (await store.ListAsync()).Count);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), revoked_at_utc FROM selected_folders WHERE id = $id GROUP BY revoked_at_utc;";
        command.Parameters.AddWithValue("$id", added.Id);
        await using var reader = await command.ExecuteReaderAsync();
        AssertEx.True(await reader.ReadAsync(), "Soft revocation must retain the selected-folder row.");
        AssertEx.Equal(expected: 1L, reader.GetInt64(0));
        AssertEx.False(await reader.IsDBNullAsync(1), "Soft revocation must stamp revoked_at_utc.");
    }

    [Test]
    public async Task AddAsync_AfterAliasWasRevoked_AllowsNewOpaqueRegistration()
    {
        var databasePath = GetDatabasePath("reuse-alias.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new NodeSelectedFolderStore(context, TimeProvider.System);
        var first = await store.AddAsync("repo-one", HostPath, SelectedFolderMode.ReadOnlyMount);
        _ = await store.RevokeAsync(first.Id);

        var replacement = await store.AddAsync("repo-one", "/trusted/host/projects/replacement", SelectedFolderMode.ReadOnlyMount);

        AssertEx.True(first.Id != replacement.Id, "Re-registration must mint a new opaque id.");
        AssertEx.Equal("repo-one", replacement.Alias);
    }

    [Test]
    public async Task GetByIdAsync_WhenHostPathTampered_FailsAuthenticatedDecryption()
    {
        var databasePath = GetDatabasePath("tamper.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid folderId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new NodeSelectedFolderStore(context, TimeProvider.System);
            var added = await store.AddAsync("tampered", HostPath, SelectedFolderMode.Copy);
            folderId = added.Id;
        }

        await TamperHostPathAsync(databasePath);

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new NodeSelectedFolderStore(readContext, TimeProvider.System);

        _ = AssertEx.Throws<CryptographicException>(() => readStore.GetByIdAsync(folderId).GetAwaiter().GetResult(),
            "A tampered host path ciphertext should fail authenticated decryption.");
    }

    [Test]
    public async Task Migrate_CreatesSelectedFoldersTableWithUniqueAliasIndex()
    {
        var databasePath = GetDatabasePath("migrate.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        var columns = await GetSelectedFolderColumnsAsync(connection);
        AssertEx.True(columns.SetEquals(new[]
            {
                "id",
                "alias",
                "host_path",
                "mode",
                "created_at_utc",
                "revoked_at_utc"
            }),
            "selected_folders should expose the mapped columns.");
        AssertEx.True(await HasActiveUniqueAliasIndexAsync(connection),
            "selected_folders.alias should be unique only among active rows.");
    }

    private static async Task TamperHostPathAsync(string databasePath)
    {
        // The test database holds exactly one selected folder, so the corruption targets that single row without
        // depending on the provider's Guid text encoding.
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        byte[] blob;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT host_path FROM selected_folders LIMIT 1;";
            blob = (byte[])(await read.ExecuteScalarAsync())!;
        }

        // Flip a byte of the trailing AES-GCM authentication tag so authenticated decryption must reject it.
        blob[^1] ^= 0xFF;

        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE selected_folders SET host_path = $blob;";
        write.Parameters.AddWithValue("$blob", blob);
        _ = await write.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlySet<string>> GetSelectedFolderColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM selected_folders LIMIT 0;";

        await using var reader = await command.ExecuteReaderAsync();
        return Enumerable.Range(start: 0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> HasActiveUniqueAliasIndexAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_selected_folders_alias';";
        var sql = await command.ExecuteScalarAsync() as string;
        return sql is not null
               && sql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               && sql.Contains("revoked_at_utc IS NULL", StringComparison.OrdinalIgnoreCase);
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .AddInterceptors(new NodeEncryptionSaveChangesInterceptor(), new NodeEncryptionMaterializationInterceptor())
                      .Options;

        return new NodeChatDbContext(options, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 1)).ToArray();
    }

    private static bool ContainsSubsequence(byte[] source, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var sourceIndex = 0; sourceIndex <= source.Length - needle.Length; sourceIndex++)
        {
            var matched = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (source[sourceIndex + needleIndex] == needle[needleIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
