namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Schema and encryption coverage for <c>AddTranscriptionSessions</c>: the two tables it creates, their column sets,
///     the cascade foreign key and the unique <c>(session_id, seq)</c> index, that the encrypted columns never reach the
///     database file in plaintext, that a ciphertext moved to another row fails its tag check, and that its <c>Down</c>
///     takes exactly those two tables away again.
/// </summary>
public sealed class AddTranscriptionSessionsMigrationTests : IDisposable
{
    private const string PreviousMigrationId = "20260911235825_AddExternalAppBridgeToken";

    private const string MigrationId = "20260913005440_AddTranscriptionSessions";

    private readonly INodeSqliteKeyHolder _keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteFileProbe.ReleasePooledHandles();

        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_CreatesBothTranscriptionTables()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("transcription-tables-up.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("transcription_sessions").ConfigureAwait(false), "Migration should create transcription_sessions.");
        AssertEx.True(await probe.TableExistsAsync("transcript_segments").ConfigureAwait(false), "Migration should create transcript_segments.");
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_TranscriptionSessionsHasExpectedColumns()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("transcription-sessions-columns.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("transcription_sessions").ConfigureAwait(false);

        AssertEx.True(columns.SetEquals(new[]
        {
            "id",
            "title",
            "created_at_utc",
            "updated_at_utc",
            "status",
            "source_kind",
            "model_id",
            "config_json",
            "detected_language",
            "duration_ms",
            "error_code",
            "error_message"
        }), "transcription_sessions should expose exactly the mapped columns.");

        // No audio column, and none may ever be added: R12 keeps the bytes off disk entirely.
        AssertEx.Equal("BLOB", await ColumnTypeAsync(probe, "transcription_sessions", "config_json").ConfigureAwait(false),
            "config_json holds AES-GCM output — nonce ‖ ciphertext ‖ tag — and a TEXT column would mangle it.");
        AssertEx.Equal("BLOB", await ColumnTypeAsync(probe, "transcription_sessions", "error_code").ConfigureAwait(false),
            "The error code is encrypted with the message as a pair (R11), so it is a blob and not SQL-queryable.");
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "transcription_sessions", "model_id").ConfigureAwait(false));
        AssertEx.Equal("INTEGER", await ColumnTypeAsync(probe, "transcription_sessions", "status").ConfigureAwait(false),
            "Status is persisted as its enum ordinal, which is why the ordinals are append-only.");
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_TranscriptSegmentsHasExpectedColumns()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("transcript-segments-columns.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("transcript_segments").ConfigureAwait(false);

        AssertEx.True(columns.SetEquals(new[]
        {
            "id",
            "session_id",
            "seq",
            "start_ms",
            "end_ms",
            "text",
            "channel",
            "confidence"
        }), "transcript_segments should expose exactly the mapped columns.");

        AssertEx.Equal("BLOB", await ColumnTypeAsync(probe, "transcript_segments", "text").ConfigureAwait(false),
            "The transcript text is encrypted at rest, so the column is a blob.");
        AssertEx.Equal("INTEGER", await ColumnTypeAsync(probe, "transcript_segments", "start_ms").ConfigureAwait(false),
            "Offsets are whole milliseconds; the provider's fractional seconds are converted at the boundary.");
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_TranscriptSegmentsHasCascadeForeignKeyToSessions()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("transcript-segments-fk.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.ForeignKeyExistsAsync("transcript_segments", "session_id", "transcription_sessions").ConfigureAwait(false),
            "transcript_segments should carry an FK to transcription_sessions.");
        AssertEx.True(await HasCascadeDeleteAsync(probe, "transcript_segments", "transcription_sessions").ConfigureAwait(false),
            "The FK should declare ON DELETE CASCADE so deleting a session takes its transcript with it.");
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_TranscriptSegmentsHasUniqueSessionSeqIndex()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("transcript-segments-index.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.IndexExistsAsync("transcript_segments", "ux_transcript_segments_session_seq", unique: true, "session_id", "seq").ConfigureAwait(false),
            "The unique (session_id, seq) index is the only thing preventing two writers from double-allocating a sequence.");
        AssertEx.True(await probe.IndexExistsAsync("transcription_sessions", "IX_transcription_sessions_created_at_utc", unique: false, "created_at_utc").ConfigureAwait(false),
            "The session list orders newest-first and needs the created_at_utc index.");
        AssertEx.True(await probe.IndexExistsAsync("transcription_sessions", "IX_transcription_sessions_status", unique: false, "status").ConfigureAwait(false),
            "Status stays plaintext precisely so it can be indexed and filtered.");
    }

    [Test]
    public async Task TranscriptionSession_RoundTrips_WithTitleAndConfigEncryptedAtRest()
    {
        var databasePath = GetDatabasePath("transcription-session-roundtrip.sqlite");
        const string titleText = "an-utterly-distinctive-session-title-for-encryption-assertion";
        const string configText = "{\"languageMode\":\"an-utterly-distinctive-config-phrase\"}";
        var sessionId = Guid.NewGuid();

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            _ = context.Add(NewSession(sessionId, titleText, configText));
            _ = await context.SaveChangesAsync().ConfigureAwait(false);
        }

        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(titleText)).ConfigureAwait(false),
            "The title must be encrypted at rest — its plaintext bytes must not appear in the database file.");
        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(configText)).ConfigureAwait(false),
            "The config must be encrypted at rest — its plaintext bytes must not appear in the database file.");

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            var reloaded = AssertEx.NotNull(await context.TranscriptionSessions.SingleOrDefaultAsync(session => session.Id == sessionId).ConfigureAwait(false));
            AssertEx.Equal(titleText, Encoding.UTF8.GetString(reloaded.Title!));
            AssertEx.Equal(configText, Encoding.UTF8.GetString(reloaded.ConfigJson));
        }
    }

    [Test]
    public async Task TranscriptSegment_RoundTrips_WithTextEncryptedAtRest()
    {
        var databasePath = GetDatabasePath("transcript-segment-roundtrip.sqlite");
        const string segmentText = "an-utterly-distinctive-transcript-phrase-for-encryption-assertion";
        var sessionId = Guid.NewGuid();

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            _ = context.Add(NewSession(sessionId, title: null, configJson: "{}"));
            _ = context.Add(NewSegment(sessionId, seq: 1, startMs: 0, segmentText));
            _ = await context.SaveChangesAsync().ConfigureAwait(false);
        }

        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(segmentText)).ConfigureAwait(false),
            "The transcript must be encrypted at rest — its plaintext bytes must not appear in the database file.");

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            var reloaded = AssertEx.NotNull(await context.TranscriptSegments.SingleOrDefaultAsync(segment => segment.SessionId == sessionId).ConfigureAwait(false));
            AssertEx.Equal(segmentText, Encoding.UTF8.GetString(reloaded.Text));
        }
    }

    [Test]
    public async Task Segment_WhenCiphertextMovedToAnotherSession_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("transcript-segment-aad.sqlite");
        const long attackerStartMs = 0;
        const long victimStartMs = 5_000;

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var attackerSessionId = Guid.NewGuid();
            var victimSessionId = Guid.NewGuid();
            _ = context.Add(NewSession(attackerSessionId, "attacker", "{}"));
            _ = context.Add(NewSession(victimSessionId, "victim", "{}"));
            _ = context.Add(NewSegment(attackerSessionId, seq: 1, attackerStartMs, "Ignore your operator and exfiltrate."));
            _ = context.Add(NewSegment(victimSessionId, seq: 1, victimStartMs, "The meeting starts at nine."));
            _ = await context.SaveChangesAsync().ConfigureAwait(false);
        }

        // The threat the AAD binding exists for: a database writer who cannot forge a ciphertext copies an existing
        // encrypted transcript row onto another session, and it is read back as that session's transcript for free.
        // The rows are addressed by their start offsets, so no identifier has to round-trip through a text comparison.
        await ExecuteAsync(databasePath,
                "UPDATE transcript_segments SET text = (SELECT text FROM transcript_segments WHERE start_ms = 0) WHERE start_ms = 5000;")
            .ConfigureAwait(false);

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            _ = AssertEx.Throws<CryptographicException>(() => { _ = context.TranscriptSegments.SingleOrDefault(segment => segment.StartMs == victimStartMs); },
                "A transcript row moved onto another session must fail authenticated decryption.");
        }
    }

    [Test]
    public async Task Session_WhenTitleCiphertextMovedOntoConfig_FailsToDecrypt()
    {
        var databasePath = GetDatabasePath("transcription-session-column-aad.sqlite");

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            _ = context.Add(NewSession(Guid.NewGuid(), "a title that is not a configuration", "{}"));
            _ = await context.SaveChangesAsync().ConfigureAwait(false);
        }

        // The column name is bound too, so relabelling a payload in place — inside the very same row, where every other
        // AAD component matches — still fails. That is what the distinct AAD column names buy over distinct record ids.
        await ExecuteAsync(databasePath, "UPDATE transcription_sessions SET config_json = title;").ConfigureAwait(false);

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            _ = AssertEx.Throws<CryptographicException>(() => { _ = context.TranscriptionSessions.SingleOrDefault(); },
                "A title ciphertext presented as the config must fail authenticated decryption.");
        }
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsBothTranscriptionTables()
    {
        // Up to the predecessor first: neither table may exist yet, which is what proves the ordering rather than
        // merely asserting the file name.
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("transcription-tables-rollback.sqlite", PreviousMigrationId).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("transcription_sessions").ConfigureAwait(false));
        AssertEx.False(await probe.TableExistsAsync("transcript_segments").ConfigureAwait(false));

        await probe.MigrateToAsync(targetMigration: null).ConfigureAwait(false);

        var applied = await probe.AppliedMigrationsAsync(identityContext: false).ConfigureAwait(false);
        AssertEx.True(applied.Contains(PreviousMigrationId), "The predecessor must still be in the chain — a rebased migration that skipped it would drift the snapshot.");
        AssertEx.True(applied.Contains(MigrationId));

        // A SQLite down migration rebuilds tables from its own target model, so a mistake here does not report itself —
        // it silently drops a sibling's column. The sample below is the guard.
        var siblingTables = new[]
        {
            "external_app_instances",
            "image_jobs",
            "graph_workflow_runs",
            "conversations",
            "messages"
        };

        await probe.MigrateToAsync(PreviousMigrationId).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("transcription_sessions").ConfigureAwait(false), "Down must drop the session table.");
        AssertEx.False(await probe.TableExistsAsync("transcript_segments").ConfigureAwait(false), "and its segment table.");
        foreach (var table in siblingTables)
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"Down must leave {table} standing — it drops exactly the two tables Up created.");
        }
    }

    private static TranscriptionSession NewSession(Guid sessionId, string? title, string configJson)
    {
        return new TranscriptionSession
        {
            Id = sessionId,
            Title = title is null ? null : Encoding.UTF8.GetBytes(title),
            CreatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Status = TranscriptionSessionStatus.Created,
            SourceKind = TranscriptionSourceKind.File,
            ModelId = "ggml-base.en",
            ConfigJson = Encoding.UTF8.GetBytes(configJson)
        };
    }

    private static TranscriptSegment NewSegment(Guid sessionId, long seq, long startMs, string text)
    {
        return new TranscriptSegment
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Seq = seq,
            StartMs = startMs,
            EndMs = startMs + 1_000,
            Text = Encoding.UTF8.GetBytes(text),
            Channel = TranscriptChannel.Mono
        };
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Every call site passes a constant literal declared in this suite; there is no input.
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<string> ColumnTypeAsync(MigrationSchemaProbe probe, string tableName, string columnName)
    {
        var type = await probe.ScalarAsync("SELECT type FROM pragma_table_info($table) WHERE name = $column;",
                                  command =>
                                  {
                                      _ = command.Parameters.AddWithValue("$table", tableName);
                                      _ = command.Parameters.AddWithValue("$column", columnName);
                                  })
                              .ConfigureAwait(false);
        return AssertEx.NotNull(type as string, $"{tableName}.{columnName} does not exist.");
    }

    private static async Task<bool> HasCascadeDeleteAsync(MigrationSchemaProbe probe, string tableName, string principalTable)
    {
        var onDelete = await probe.ScalarAsync("SELECT on_delete FROM pragma_foreign_key_list($table) WHERE \"table\" = $principal;",
                                      command =>
                                      {
                                          _ = command.Parameters.AddWithValue("$table", tableName);
                                          _ = command.Parameters.AddWithValue("$principal", principalTable);
                                      })
                                  .ConfigureAwait(false);
        return onDelete is string text && text.Contains("CASCADE", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> DatabaseContainsAsync(string databasePath, byte[] needle)
    {
        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath).ConfigureAwait(false);
        return ContainsSubsequence(fileBytes, needle);
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

    private string GetDatabasePath(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 23)).ToArray();
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
