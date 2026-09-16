namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;

/// <summary>
///     Pre-migrated SQLite files that a suite copies instead of replaying the whole declared migration chain against an
///     empty database. A copy is a file-system operation; a replay walks every declared migration's DDL, which is the
///     dominant per-test cost in this project.
///     <para>
///         Twin of <c>XE-Local-AI-Engine.Tests/TestServerWebAppFactory.BuildMigratedTemplate</c>, which does the same
///         thing for that project's host fixture. The two are deliberately separate copies: this project does not
///         reference the host project, and a shared abstraction for two consumers would cost more than the duplication.
///     </para>
///     <para>
///         A template outlives the process — the next test run reuses it — so the file name carries the migrations
///         assembly's module version id and a rebuild of that assembly yields a new name. A stale template can never be
///         picked up; delete the files to force a rebuild. The key covers the migrations assembly only, not the builder
///         seams it migrates through (<see cref="MigrationSchemaProbe.ApplyChatAsync" />,
///         <see cref="MigrationSchemaProbe.ApplyIdentityAsync" />, <c>AgentDefinitionTestContextFactory.CreateForMigration</c>):
///         a change in one of those that altered the produced bytes would reuse a stale template, so bump the key by hand
///         if that ever happens.
///     </para>
///     <para>
///         Cost of outliving the process: one file per state, on the order of fifty under
///         <c>$TMPDIR/xe-local-ai-engine-persistence-template-*</c>, tens of megabytes in total, and every rebuild of
///         the persistence assembly orphans the whole set. Do NOT add automatic cleanup. Two worktrees running tests
///         concurrently sit on two different module version ids, so a sweep that deletes "the other" ids would delete a
///         live run's templates out from under it — the same cross-worktree hazard <c>AGENTS.md</c> bans
///         <c>pkill -f</c> for. Delete them by hand when the disk matters.
///     </para>
/// </summary>
internal static class MigratedDatabaseTemplate
{
    /// <summary>
    ///     Both contexts' migrations live in the same assembly, so one module version id keys every template here.
    /// </summary>
    private static readonly string MigrationsAssemblyKey =
        typeof(NodeChatDbContext).Assembly.ManifestModule.ModuleVersionId.ToString("N", CultureInfo.InvariantCulture);

    private static readonly Lazy<Task<string>> ChatHead =
        new(() => BuildAsync("chat-head", path => MigrationSchemaProbe.ApplyChatAsync(path, targetMigration: null)),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Task<string>> IdentityHead =
        new(() => BuildAsync("identity-head", MigrationSchemaProbe.ApplyIdentityAsync), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> ChatAtTargets = new(StringComparer.Ordinal);

    /// <summary>
    ///     The declared chat migration ids, read once off the migrations assembly. <see cref="CopyChatAtAsync" /> checks
    ///     its target against this set: every distinct target costs one full chain replay to build, so a caller that
    ///     keys a template on something variable — a file name, a GUID — would silently reintroduce exactly the cost the
    ///     templates remove. An undeclared id is that mistake, and it is caught on the first call instead of after
    ///     dozens of replays. It also bounds the number of templates by the chain length by construction.
    /// </summary>
    private static readonly Lazy<IReadOnlySet<string>> DeclaredChatMigrations =
        new(ReadDeclaredChatMigrations, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Writes a <see cref="NodeChatDbContext" /> database migrated to head at <paramref name="databasePath" />.</summary>
    public static async Task CopyChatHeadAsync(string databasePath)
    {
        Publish(await ChatHead.Value.ConfigureAwait(false), databasePath);
    }

    /// <summary>Writes a <see cref="NodeIdentityDbContext" /> database migrated to head at <paramref name="databasePath" />.</summary>
    public static async Task CopyIdentityHeadAsync(string databasePath)
    {
        Publish(await IdentityHead.Value.ConfigureAwait(false), databasePath);
    }

    /// <summary>
    ///     Writes a <see cref="NodeChatDbContext" /> database whose chain stops at <paramref name="targetMigrationId" />
    ///     — the state a suite needs before it seeds historical rows and runs the next migration over them for real.
    ///     <c>__EFMigrationsHistory</c> travels with the file, so the migrator sees exactly the state a replay to that
    ///     id would have left.
    /// </summary>
    public static async Task CopyChatAtAsync(string databasePath, string targetMigrationId)
    {
        EnsureDeclaredChatMigration(targetMigrationId);

        var template = ChatAtTargets.GetOrAdd(targetMigrationId,
            static id => new Lazy<Task<string>>(() => BuildAsync($"chat-at-{id}", path => MigrationSchemaProbe.ApplyChatAsync(path, id)),
                LazyThreadSafetyMode.ExecutionAndPublication));

        Publish(await template.Value.ConfigureAwait(false), databasePath);
    }

    /// <summary>
    ///     The <see cref="CopyChatAtAsync" /> guard on its own, for a caller that can refuse the bad target before it
    ///     allocates anything of its own.
    /// </summary>
    internal static void EnsureDeclaredChatMigration(string targetMigrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMigrationId);

        if (!DeclaredChatMigrations.Value.Contains(targetMigrationId))
        {
            throw new InvalidOperationException($"'{targetMigrationId}' is not a declared chat migration id, so there is no chain state to build a template for. "
                                                + "Pass the id of a declared migration: a target derived from anything else — a file name, a GUID — costs a full "
                                                + "chain replay per caller and reintroduces exactly the cost these templates exist to remove.");
        }
    }

    /// <summary>
    ///     The published chat head template file. Only <c>MigratedDatabaseTemplateTests</c> needs it, to assert the
    ///     naming key and the no-sidecar invariant on the published file rather than on a copy of it.
    /// </summary>
    internal static Task<string> ChatHeadPathAsync()
    {
        return ChatHead.Value;
    }

    // Builds one template and publishes it under its key-derived name.
    //
    // Invariants, because a single-file copy is only valid while both hold:
    //  - The write-ahead log is checkpointed (PRAGMA wal_checkpoint(TRUNCATE)) and every pooled connection to the
    //    scratch file is released (SqliteConnection.ClearPool) before the publish, so the last close folds the log
    //    back into the main file and takes the sidecars with it. The template IS a WAL database and that is fine:
    //    EF Core's SqliteDatabaseCreator.Create turns WAL on when it creates the file, independently of the product
    //    pragma interceptor this build does not use, and journal_mode is a persistent file property, so the copy
    //    carries it. What would lose committed rows is copying the main file while a `-wal` still holds them.
    //  - No `-wal`/`-shm` sidecar exists next to the finished file. Asserted, not assumed: the day a change leaves a
    //    connection open across the publish, or skips the checkpoint, a silent copy would lose committed rows.
    //
    // The publish is a same-filesystem rename, i.e. atomic; a losing racer (another test process on the same build)
    // simply keeps the winner's equivalent template.
    private static async Task<string> BuildAsync(string key, Func<string, Task> applyAsync)
    {
        var templatePath = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-persistence-template-{MigrationsAssemblyKey}-{key}.sqlite");
        if (File.Exists(templatePath))
        {
            return templatePath;
        }

        // A directory, not a bare file: the migration path can drop sidecars next to the database, and one recursive
        // delete takes the whole family with it.
        var scratchDirectory = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-persistence-template-build-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(scratchDirectory);
        try
        {
            var scratchDatabase = Path.Combine(scratchDirectory, "template.sqlite");

            await applyAsync(scratchDatabase).ConfigureAwait(false);
            await CheckpointAndReleaseAsync(scratchDatabase).ConfigureAwait(false);

            foreach (var sidecar in new[]
                     {
                         scratchDatabase + "-wal",
                         scratchDatabase + "-shm"
                     })
            {
                if (File.Exists(sidecar))
                {
                    throw new InvalidOperationException($"The migrated template left {Path.GetFileName(sidecar)} behind, so copying the database file alone would lose committed rows. "
                                                        + "A published template has to be checkpointed and fully closed — being in WAL mode is expected, an outstanding sidecar is not.");
                }
            }

            try
            {
                File.Move(scratchDatabase, templatePath);
            }
            catch (IOException) when (File.Exists(templatePath))
            {
                // Another test process published the same-keyed template first; use theirs. The filter is what makes
                // that comment true: without it a full disk or a read-only temp directory is swallowed too, and the
                // method returns a path that does not exist. Its twin is
                // TestServerWebAppFactory.BuildMigratedTemplate in XE-Local-AI-Engine.Tests, which carries the same
                // filter for the same reason; the two must stay in step.
            }
        }
        finally
        {
            Directory.Delete(scratchDirectory, recursive: true);
        }

        return templatePath;
    }

    private static IReadOnlySet<string> ReadDeclaredChatMigrations()
    {
        var probeDirectory = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-persistence-declared-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(probeDirectory);
        using var keyHolder = new NullNodeSqliteKeyHolder();

        try
        {
            // Reading the declared set touches no database file; the context is only the route to the migrations
            // assembly. CreateForMigration creates the parent directory, which is why this owns a throwaway one.
            using var context = AgentDefinitionTestContextFactory.CreateForMigration(Path.Combine(probeDirectory, "declared.sqlite"), keyHolder);
            return context.Database.GetService<IMigrationsAssembly>().Migrations.Keys.ToHashSet(StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(probeDirectory, recursive: true);
        }
    }

    private static async Task CheckpointAndReleaseAsync(string databasePath)
    {
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                // The migrated file is in WAL mode — EF Core's SqliteDatabaseCreator.Create enables it — so this is a
                // real checkpoint, not a formality: it folds the log back into the main file the publish will copy.
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Scoped to this connection string, never the process-global ClearAllPools: that one reaches every other
            // test class's pool too, and discarding pools this build has nothing to do with buys nothing here.
            SqliteConnection.ClearPool(connection);
        }
    }

    private static void Publish(string templatePath, string databasePath)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        // overwrite: false on purpose. Overwriting a path that already holds a database replaces the main file and
        // leaves that database's -wal/-shm beside it, which is the corrupt-read shape the sidecar invariant exists to
        // prevent. Nothing copies twice onto one path today, and this turns the next such attempt into an IOException
        // instead of a silent corruption.
        File.Copy(templatePath, databasePath, overwrite: false);
    }
}
