namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Behaviour of <see cref="TranscriptionSessionStore" /> against a real on-disk SQLite file with the real node
///     encryption interceptors. The cipher is never substituted: what these assert is that the round trip through it
///     works, that the list pages and orders the way the session list depends on, that deleting a session takes its
///     transcript with it, and that the unique <c>(session_id, seq)</c> index rejects a repeated sequence.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TranscriptionSessionStoreTests : IDisposable
{
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
    public async Task Create_ThenGetWithSegments_RoundTripsDecrypted()
    {
        var databasePath = await CreateSchemaAsync("store-roundtrip.sqlite");
        var sessionId = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "Kick-off call", "{\"translate\":false}", createdAtUtc: 1_000), CancellationToken.None));
        await RunAsync(databasePath,
            store => store.AppendSegmentsAsync(sessionId,
                [
                    NewSegment(seq: 1, startMs: 0, "Good morning."),
                    NewSegment(seq: 2, startMs: 1_500, "Let us begin.", TranscriptChannel.Others, confidence: 0.87)
                ],
                updatedAtUtc: 2_000,
                CancellationToken.None));

        var detail = await QueryAsync(databasePath, store => store.GetWithSegmentsAsync(sessionId, CancellationToken.None));

        var view = AssertEx.NotNull(detail, "The session should be readable after creation.");
        AssertEx.Equal("Kick-off call", view.Title);
        AssertEx.Equal("{\"translate\":false}", view.ConfigJson);
        AssertEx.Equal(TranscriptionSessionStatus.Created, view.Status);
        AssertEx.Equal(TranscriptionSourceKind.File, view.SourceKind);
        AssertEx.Equal("ggml-base.en", view.ModelId);
        AssertEx.Equal(expected: 1_000L, view.CreatedAtUtc);
        AssertEx.Equal(expected: 2_000L, view.UpdatedAtUtc, "The append carried the session's updated stamp forward.");
        AssertEx.Equal(expected: 2, view.Segments.Count);
        AssertEx.Equal(expected: 2, view.SegmentCount);
        AssertEx.Equal("Good morning.", view.Segments[0].Text);
        AssertEx.Equal(TranscriptChannel.Mono, view.Segments[0].Channel);
        AssertEx.Equal("Let us begin.", view.Segments[1].Text);
        AssertEx.Equal(TranscriptChannel.Others, view.Segments[1].Channel);
        AssertEx.Equal(expected: 0.87, view.Segments[1].Confidence ?? double.NaN);
        AssertEx.Equal(expected: 1_500L, view.Segments[1].StartMs);
    }

    [Test]
    public async Task List_OrdersNewestFirst_AndPagesByLimitOffset()
    {
        var databasePath = await CreateSchemaAsync("store-list.sqlite");

        // The middle pair share a creation millisecond on purpose: the descending id tiebreak is what keeps them from
        // shuffling between pages, and without it this test is the one that would flake.
        var oldest = Guid.NewGuid();
        var tiedLower = new Guid("11111111-1111-1111-1111-111111111111");
        var tiedHigher = new Guid("22222222-2222-2222-2222-222222222222");
        var newest = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(oldest, "oldest", "{}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(tiedLower, "tied-lower", "{}", createdAtUtc: 200), CancellationToken.None));
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(tiedHigher, "tied-higher", "{}", createdAtUtc: 200), CancellationToken.None));
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(newest, "newest", "{}", createdAtUtc: 300), CancellationToken.None));

        // Two rows on the newest session and none on the rest, so a count that came from the wrong row shows up.
        await RunAsync(databasePath,
            store => store.AppendSegmentsAsync(newest,
                [
                    NewSegment(seq: 1, startMs: 0, "one"),
                    NewSegment(seq: 2, startMs: 1_000, "two")
                ],
                updatedAtUtc: 350,
                CancellationToken.None));

        var firstPage = await QueryAsync(databasePath, store => store.ListAsync(limit: 2, offset: 0, CancellationToken.None));
        var secondPage = await QueryAsync(databasePath, store => store.ListAsync(limit: 2, offset: 2, CancellationToken.None));
        var negativeBounds = await QueryAsync(databasePath, store => store.ListAsync(limit: -1, offset: -5, CancellationToken.None));

        AssertEx.Equal(expected: 2, firstPage.Count);
        AssertEx.Equal(newest, firstPage[0].Id, "Newest first.");
        AssertEx.Equal(tiedHigher, firstPage[1].Id, "Within one millisecond the higher id comes first.");
        AssertEx.Equal(expected: 2, secondPage.Count);
        AssertEx.Equal(tiedLower, secondPage[0].Id);
        AssertEx.Equal(oldest, secondPage[1].Id);
        AssertEx.Equal("newest", firstPage[0].Title, "The summary decrypts the title.");
        AssertEx.Equal(expected: 2, firstPage[0].SegmentCount, "The count is read in SQL beside the session, not from loaded rows.");
        AssertEx.Equal(expected: 0, firstPage[1].SegmentCount, "A session with no transcript counts zero rather than reporting its neighbour's.");

        // A negative limit reaches SQLite as LIMIT -1, which means "no limit" — the whole table, every title decrypted.
        AssertEx.Empty(negativeBounds, "Negative page bounds must floor to an empty page, never to the entire table.");
    }

    [Test]
    public async Task Count_IgnoresLimitAndOffset()
    {
        var databasePath = await CreateSchemaAsync("store-count.sqlite");

        for (var index = 0; index < 3; index++)
        {
            var createdAtUtc = 100 + index;
            await RunAsync(databasePath, store => store.CreateAsync(NewCreate(Guid.NewGuid(), $"s{createdAtUtc}", "{}", createdAtUtc), CancellationToken.None));
        }

        var page = await QueryAsync(databasePath, store => store.ListAsync(limit: 1, offset: 1, CancellationToken.None));
        var total = await QueryAsync(databasePath, store => store.CountAsync(CancellationToken.None));

        AssertEx.Equal(expected: 1, page.Count, "The page honours the limit.");
        AssertEx.Equal(expected: 3, total, "The count is the total, not the page size — it is what drives the pager.");
    }

    [Test]
    public async Task Delete_CascadesSegments()
    {
        var databasePath = await CreateSchemaAsync("store-delete.sqlite");
        var deletedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(deletedId, "doomed", "{}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(survivorId, "kept", "{}", createdAtUtc: 200), CancellationToken.None));
        await RunAsync(databasePath, store => store.AppendSegmentsAsync(deletedId, [NewSegment(seq: 1, startMs: 0, "doomed text")], updatedAtUtc: 150, CancellationToken.None));
        await RunAsync(databasePath, store => store.AppendSegmentsAsync(survivorId, [NewSegment(seq: 1, startMs: 0, "kept text")], updatedAtUtc: 250, CancellationToken.None));

        var deleted = await QueryAsync(databasePath, store => store.DeleteAsync(deletedId, CancellationToken.None));
        var missing = await QueryAsync(databasePath, store => store.DeleteAsync(deletedId, CancellationToken.None));

        AssertEx.True(deleted, "Delete should report a removed row.");
        AssertEx.False(missing, "Deleting an unknown session reports false rather than throwing.");
        AssertEx.Null(await QueryAsync(databasePath, store => store.GetWithSegmentsAsync(deletedId, CancellationToken.None)));

        // The rows are gone from the table itself, not merely hidden behind the session read.
        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);
        AssertEx.Equal(expected: 0, await context.TranscriptSegments.CountAsync(segment => segment.SessionId == deletedId),
            "Deleting a session must take its transcript rows with it.");
        AssertEx.Equal(expected: 1, await context.TranscriptSegments.CountAsync(segment => segment.SessionId == survivorId),
            "and must leave every other session's transcript standing.");
        AssertEx.Equal(expected: 0, await context.TranscriptionSessions.CountAsync(session => session.Id == deletedId),
            "The session row goes with its transcript, in the same transaction.");
    }

    [Test]
    public async Task AppendSegments_AssignsRowsAndReadsBackOrderedBySeq()
    {
        var databasePath = await CreateSchemaAsync("store-append.sqlite");
        var sessionId = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "ordering", "{}", createdAtUtc: 100), CancellationToken.None));

        // Written out of order so the read path's ordering is what is being asserted, not the insertion order.
        await RunAsync(databasePath,
            store => store.AppendSegmentsAsync(sessionId,
                [
                    NewSegment(seq: 3, startMs: 4_000, "third"),
                    NewSegment(seq: 1, startMs: 0, "first")
                ],
                updatedAtUtc: 200,
                CancellationToken.None));
        await RunAsync(databasePath, store => store.AppendSegmentsAsync(sessionId, [NewSegment(seq: 2, startMs: 2_000, "second")], updatedAtUtc: 300, CancellationToken.None));
        var emptyAppend = await QueryAsync(databasePath, store => store.AppendSegmentsAsync(sessionId, [], updatedAtUtc: 400, CancellationToken.None));

        var view = AssertEx.NotNull(await QueryAsync(databasePath, store => store.GetWithSegmentsAsync(sessionId, CancellationToken.None)));

        AssertEx.True(emptyAppend, "An empty append against a known session reports success.");
        AssertEx.Equal(expected: 3, view.Segments.Count, "An empty append writes nothing and is not an error.");
        AssertEx.Equal(expected: 3, view.SegmentCount, "The detail view's count matches the rows it carries.");
        AssertEx.Equal(expected: 300L, view.UpdatedAtUtc, "An append bumps the session's updated stamp; the empty one leaves it alone.");
        AssertEx.Equal("first", view.Segments[0].Text);
        AssertEx.Equal("second", view.Segments[1].Text);
        AssertEx.Equal("third", view.Segments[2].Text);
        AssertEx.True(view.Segments.Select(segment => segment.Seq).SequenceEqual([1L, 2L, 3L]), "Segments read back ascending by sequence.");
        AssertEx.Equal(expected: 3, view.Segments.Select(segment => segment.Id).Distinct().Count(), "Every appended row gets its own identity.");
    }

    [Test]
    public async Task AppendSegments_WhenSeqRepeats_Throws()
    {
        var databasePath = await CreateSchemaAsync("store-append-duplicate.sqlite");
        var sessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "duplicate", "{}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(otherSessionId, "other", "{}", createdAtUtc: 200), CancellationToken.None));
        await RunAsync(databasePath, store => store.AppendSegmentsAsync(sessionId, [NewSegment(seq: 1, startMs: 0, "first")], updatedAtUtc: 150, CancellationToken.None));

        // The unique (session_id, seq) index is the guard, not a check in the store: two writers that both believe they
        // own sequence 1 must collide at the database rather than silently interleave.
        _ = await AssertEx.ThrowsAsync<DbUpdateException>(
            () => RunAsync(databasePath, store => store.AppendSegmentsAsync(sessionId, [NewSegment(seq: 1, startMs: 9_000, "collision")], updatedAtUtc: 160, CancellationToken.None)),
            "A repeated sequence within one session must be rejected.");

        // The index is scoped to the session, so the same sequence in another session is perfectly legal.
        await RunAsync(databasePath, store => store.AppendSegmentsAsync(otherSessionId, [NewSegment(seq: 1, startMs: 0, "independent")], updatedAtUtc: 250, CancellationToken.None));

        var view = AssertEx.NotNull(await QueryAsync(databasePath, store => store.GetWithSegmentsAsync(sessionId, CancellationToken.None)));
        AssertEx.Equal(expected: 1, view.Segments.Count, "The rejected batch must not have landed.");
    }

    [Test]
    public async Task AppendSegments_WhenSessionUnknown_WritesNothing()
    {
        var databasePath = await CreateSchemaAsync("store-append-orphan.sqlite");
        var unknownSessionId = Guid.NewGuid();

        // The segment's session reference would refuse the insert, but as a constraint exception rather than an answer:
        // the store's own existence check is what turns an unknown session into `false` before any write is attempted.
        var appended = await QueryAsync(databasePath,
            store => store.AppendSegmentsAsync(unknownSessionId, [NewSegment(seq: 1, startMs: 0, "orphan")], updatedAtUtc: 100, CancellationToken.None));

        AssertEx.False(appended, "Appending to an unknown session reports false rather than writing orphans.");

        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);
        AssertEx.Equal(expected: 0, await context.TranscriptSegments.CountAsync(), "No orphan transcript row may exist.");
    }

    [Test]
    public async Task Fail_StoresErrorCodeAndMessageEncrypted()
    {
        var databasePath = await CreateSchemaAsync("store-fail.sqlite");
        var sessionId = Guid.NewGuid();
        const string errorCode = "an-utterly-distinctive-error-code-token";
        const string errorMessage = "an-utterly-distinctive-error-message-phrase";
        const string configText = "{\"an-utterly-distinctive-config\":true}";

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "failing", configText, createdAtUtc: 100), CancellationToken.None));

        var transcribing = await QueryAsync(databasePath, store => store.SetStatusAsync(sessionId, TranscriptionSessionStatus.Transcribing, updatedAtUtc: 150, CancellationToken.None));
        var failed = await QueryAsync(databasePath, store => store.FailAsync(sessionId, errorCode, errorMessage, updatedAtUtc: 200, CancellationToken.None));
        var unknown = await QueryAsync(databasePath, store => store.FailAsync(Guid.NewGuid(), errorCode, errorMessage, updatedAtUtc: 200, CancellationToken.None));

        AssertEx.True(transcribing);
        AssertEx.True(failed);
        AssertEx.False(unknown, "Failing an unknown session reports false rather than throwing.");

        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(errorCode)),
            "The error code is encrypted at rest — its plaintext must not appear in the database file.");
        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(errorMessage)),
            "The error message is encrypted at rest — its plaintext must not appear in the database file.");
        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(configText)),
            "A status-only update must leave the config ciphertext intact rather than rewriting it in the clear.");

        var view = AssertEx.NotNull(await QueryAsync(databasePath, store => store.GetWithSegmentsAsync(sessionId, CancellationToken.None)));
        AssertEx.Equal(TranscriptionSessionStatus.Failed, view.Status);
        AssertEx.Equal(errorCode, view.ErrorCode, "The failure detail is on the detail view only; the session list never carries it.");
        AssertEx.Equal(errorMessage, view.ErrorMessage);
        AssertEx.Equal(expected: 200L, view.UpdatedAtUtc);
        AssertEx.Equal(configText, view.ConfigJson, "The config must still decrypt after two status writes over it.");
    }

    [Test]
    public async Task Complete_RecordsDetectedLanguageAndDuration()
    {
        var databasePath = await CreateSchemaAsync("store-complete.sqlite");
        var sessionId = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "completing", "{}", createdAtUtc: 100), CancellationToken.None));

        var completed = await QueryAsync(databasePath, store => store.CompleteAsync(sessionId, "en", durationMs: 12_345, updatedAtUtc: 400, CancellationToken.None));
        var unknown = await QueryAsync(databasePath, store => store.CompleteAsync(Guid.NewGuid(), "en", durationMs: 1, updatedAtUtc: 400, CancellationToken.None));

        AssertEx.True(completed);
        AssertEx.False(unknown, "Completing an unknown session reports false rather than throwing.");

        var summaries = await QueryAsync(databasePath, store => store.ListAsync(limit: 10, offset: 0, CancellationToken.None));
        var summary = AssertEx.NotNull(summaries.SingleOrDefault(item => item.Id == sessionId));
        AssertEx.Equal(TranscriptionSessionStatus.Completed, summary.Status);
        AssertEx.Equal("en", summary.DetectedLanguage);
        AssertEx.Equal(expected: 12_345L, summary.DurationMs ?? -1L);
        AssertEx.Equal(expected: 400L, summary.UpdatedAtUtc);
    }

    [Test]
    public async Task ListSegmentsAfter_PagesFromTheWatermarkAndExcludesIt()
    {
        var databasePath = await CreateSchemaAsync("store-segments-after.sqlite");
        var sessionId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "live", "{}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(otherId, "other", "{}", createdAtUtc: 200), CancellationToken.None));

        // Appended out of sequence order on purpose: the read orders by seq, not by insertion.
        await RunAsync(databasePath,
            store => store.AppendSegmentsAsync(sessionId,
                [
                    NewSegment(seq: 3, startMs: 2_000, "three"),
                    NewSegment(seq: 1, startMs: 0, "one"),
                    NewSegment(seq: 4, startMs: 3_000, "four", TranscriptChannel.Others),
                    NewSegment(seq: 2, startMs: 1_000, "two")
                ],
                updatedAtUtc: 300,
                CancellationToken.None));

        // A row on a second session, to prove the read is keyed on the session and not on the sequence alone.
        await RunAsync(databasePath,
            store => store.AppendSegmentsAsync(otherId, [NewSegment(seq: 1, startMs: 0, "not mine")], updatedAtUtc: 300, CancellationToken.None));

        var fromStart = await QueryAsync(databasePath, store => store.ListSegmentsAfterAsync(sessionId, afterSeq: 0, limit: 2, CancellationToken.None));
        var afterTwo = await QueryAsync(databasePath, store => store.ListSegmentsAfterAsync(sessionId, afterSeq: 2, limit: 10, CancellationToken.None));
        var afterLast = await QueryAsync(databasePath, store => store.ListSegmentsAfterAsync(sessionId, afterSeq: 4, limit: 10, CancellationToken.None));

        AssertEx.Equal("1:one,2:two", string.Join(',', fromStart.Select(static row => $"{row.Seq}:{row.Text}")),
            "A fresh subscriber reads the first page in sequence order, decrypted, and stops at the limit.");
        AssertEx.Equal("3:three,4:four", string.Join(',', afterTwo.Select(static row => $"{row.Seq}:{row.Text}")),
            "The watermark is an EXCLUSIVE lower bound: sequence two is the last row seen, never a row to resend.");
        AssertEx.Equal(TranscriptChannel.Others, afterTwo[^1].Channel, "The channel survives the read.");
        AssertEx.Empty(afterLast, "A subscriber already at the end of the transcript is handed nothing.");
    }

    [Test]
    public async Task ListSegmentsAfter_ForAnUnknownSession_IsEmpty()
    {
        var databasePath = await CreateSchemaAsync("store-segments-after-unknown.sqlite");

        var rows = await QueryAsync(databasePath, store => store.ListSegmentsAfterAsync(Guid.NewGuid(), afterSeq: 0, limit: 10, CancellationToken.None));

        AssertEx.Empty(rows, "An unknown session reads as an empty transcript, never as a throw — a persist-free live session has no row at all.");
    }

    [Test]
    public async Task TryTransitionStatus_OnlyMovesTheRowFromTheExpectedStatus()
    {
        var databasePath = await CreateSchemaAsync("store-transition.sqlite");
        var sessionId = Guid.NewGuid();
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "live", "{}", createdAtUtc: 100), CancellationToken.None));

        var moved = await QueryAsync(databasePath,
            store => store.TryTransitionStatusAsync(sessionId,
                TranscriptionSessionStatus.Created,
                TranscriptionSessionStatus.Transcribing,
                updatedAtUtc: 200,
                CancellationToken.None));

        // Someone else has since finished the session; a caller still holding the stale Created must not resurrect it.
        _ = await QueryAsync(databasePath, store => store.SetStatusAsync(sessionId, TranscriptionSessionStatus.Completed, updatedAtUtc: 300, CancellationToken.None));
        var stale = await QueryAsync(databasePath,
            store => store.TryTransitionStatusAsync(sessionId,
                TranscriptionSessionStatus.Created,
                TranscriptionSessionStatus.Transcribing,
                updatedAtUtc: 400,
                CancellationToken.None));

        var unknown = await QueryAsync(databasePath,
            store => store.TryTransitionStatusAsync(Guid.NewGuid(),
                TranscriptionSessionStatus.Created,
                TranscriptionSessionStatus.Transcribing,
                updatedAtUtc: 500,
                CancellationToken.None));

        var view = AssertEx.NotNull(await QueryAsync(databasePath, store => store.GetSummaryAsync(sessionId, CancellationToken.None)), "The session exists.");
        AssertEx.True(moved, "A row in the expected status moves.");
        AssertEx.False(stale, "A row that has moved on does not.");
        AssertEx.False(unknown, "Neither does one that does not exist.");
        AssertEx.Equal(TranscriptionSessionStatus.Completed, view.Status, "The losing transition wrote nothing at all.");
        AssertEx.Equal(expected: 300L, view.UpdatedAtUtc, "Not even the timestamp.");
    }

    [Test]
    public async Task GetSummary_ReadsTheSessionWithoutItsTranscript_AndCountsIt()
    {
        var databasePath = await CreateSchemaAsync("store-summary.sqlite");
        var sessionId = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "titled", "{\"translate\":true}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath,
            store => store.AppendSegmentsAsync(sessionId,
                [NewSegment(seq: 1, startMs: 0, "one"), NewSegment(seq: 2, startMs: 1_000, "two")],
                updatedAtUtc: 200,
                CancellationToken.None));

        var summary = AssertEx.NotNull(await QueryAsync(databasePath, store => store.GetSummaryAsync(sessionId, CancellationToken.None)), "The session is readable.");
        var unknown = await QueryAsync(databasePath, store => store.GetSummaryAsync(Guid.NewGuid(), CancellationToken.None));

        AssertEx.Equal("titled", summary.Title, "The title decrypts.");
        AssertEx.Equal("{\"translate\":true}", summary.ConfigJson, "And so does the config the live start reads.");
        AssertEx.Equal(expected: 2, summary.SegmentCount, "The count is the transcript's length, counted in SQL.");
        AssertEx.Null(unknown, "An unknown id reads as null rather than throwing.");
    }

    [Test]
    public async Task GetLastSeq_ReturnsTheMaximum_NotTheCount()
    {
        var databasePath = await CreateSchemaAsync("store-lastseq.sqlite");
        var sessionId = Guid.NewGuid();
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "gappy", "{}", createdAtUtc: 100), CancellationToken.None));

        var empty = await QueryAsync(databasePath, store => store.GetLastSeqAsync(sessionId, CancellationToken.None));

        // A deliberate gap: two rows numbered 1 and 7. A count would answer 2 and re-allocate a sequence the unique
        // (session_id, seq) index already holds.
        await RunAsync(databasePath,
            store => store.AppendSegmentsAsync(sessionId,
                [NewSegment(seq: 1, startMs: 0, "one"), NewSegment(seq: 7, startMs: 6_000, "seven")],
                updatedAtUtc: 200,
                CancellationToken.None));

        var last = await QueryAsync(databasePath, store => store.GetLastSeqAsync(sessionId, CancellationToken.None));
        var unknown = await QueryAsync(databasePath, store => store.GetLastSeqAsync(Guid.NewGuid(), CancellationToken.None));

        AssertEx.Equal(expected: 0L, empty, "A transcript with no rows starts the live counter at zero.");
        AssertEx.Equal(expected: 7L, last, "The maximum, not the count of two.");
        AssertEx.Equal(expected: 0L, unknown, "An unknown session has no sequences.");
    }

    [Test]
    public async Task UpdateSegmentText_ReEncryptsTheRow_AndDecryptsBackThroughTheDetailRead()
    {
        var databasePath = await CreateSchemaAsync("store-update-segment.sqlite");
        var sessionId = Guid.NewGuid();
        const string original = "an-utterly-distinctive-misheard-phrase";
        const string corrected = "an-utterly-distinctive-corrected-phrase";

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "edit", "{}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath,
            store => store.AppendSegmentsAsync(sessionId, [NewSegment(seq: 1, startMs: 0, "untouched"), NewSegment(seq: 2, startMs: 1_000, original)], updatedAtUtc: 200, CancellationToken.None));
        var cipherBefore = await ReadRawSegmentTextAsync(databasePath, seq: 2);

        var outcome = await QueryAsync(databasePath, store => store.UpdateSegmentTextAsync(sessionId, seq: 2, corrected, updatedAtUtc: 300, CancellationToken.None));

        AssertEx.Equal(TranscriptSegmentUpdateOutcome.Updated, outcome);
        var cipherAfter = await ReadRawSegmentTextAsync(databasePath, seq: 2);
        AssertEx.False(cipherBefore.AsSpan().SequenceEqual(cipherAfter), "The stored ciphertext must change with the text.");
        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(corrected)),
            "The edited text is encrypted at rest — its plaintext must not appear in the database file.");

        var view = AssertEx.NotNull(await QueryAsync(databasePath, store => store.GetWithSegmentsAsync(sessionId, CancellationToken.None)));
        AssertEx.Equal("untouched", view.Segments[0].Text, "Only the addressed row changes.");
        AssertEx.Equal(corrected, view.Segments[1].Text);
        AssertEx.Equal(expected: 300L, view.UpdatedAtUtc, "The edit bumps the session's updated stamp.");
        AssertEx.Equal("{}", view.ConfigJson, "The session's own ciphertext survives the edit.");
    }

    [Test]
    public async Task UpdateSegmentText_ForAnUnknownSessionOrSeq_WritesNothing()
    {
        var databasePath = await CreateSchemaAsync("store-update-segment-missing.sqlite");
        var sessionId = Guid.NewGuid();

        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "edit", "{}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath, store => store.AppendSegmentsAsync(sessionId, [NewSegment(seq: 1, startMs: 0, "kept")], updatedAtUtc: 200, CancellationToken.None));

        var unknownSession = await QueryAsync(databasePath, store => store.UpdateSegmentTextAsync(Guid.NewGuid(), seq: 1, "x", updatedAtUtc: 300, CancellationToken.None));
        var unknownSeq = await QueryAsync(databasePath, store => store.UpdateSegmentTextAsync(sessionId, seq: 9, "x", updatedAtUtc: 300, CancellationToken.None));

        AssertEx.Equal(TranscriptSegmentUpdateOutcome.SessionNotFound, unknownSession);
        AssertEx.Equal(TranscriptSegmentUpdateOutcome.SegmentNotFound, unknownSeq);
        var view = AssertEx.NotNull(await QueryAsync(databasePath, store => store.GetWithSegmentsAsync(sessionId, CancellationToken.None)));
        AssertEx.Equal("kept", view.Segments[0].Text);
        AssertEx.Equal(expected: 200L, view.UpdatedAtUtc, "A refused edit must not bump the session's updated stamp.");
    }

    [Test]
    public async Task UpdateSegmentText_WhenTheSegmentIsDeletedBetweenLoadAndSave_ReportsSegmentNotFound()
    {
        var databasePath = await CreateSchemaAsync("store-update-segment-race.sqlite");
        var sessionId = Guid.NewGuid();
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "edit", "{}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath, store => store.AppendSegmentsAsync(sessionId, [NewSegment(seq: 1, startMs: 0, "doomed")], updatedAtUtc: 200, CancellationToken.None));

        var outcome = await UpdateRacingADeleteAsync(databasePath, sessionId, async other =>
            await other.TranscriptSegments.Where(row => row.SessionId == sessionId).ExecuteDeleteAsync());

        // Without the mapping the save throws DbUpdateConcurrencyException, which the endpoint turns into a 500.
        AssertEx.Equal(TranscriptSegmentUpdateOutcome.SegmentNotFound, outcome);
    }

    [Test]
    public async Task UpdateSegmentText_WhenTheSessionIsDeletedBetweenLoadAndSave_ReportsSessionNotFound()
    {
        var databasePath = await CreateSchemaAsync("store-update-session-race.sqlite");
        var sessionId = Guid.NewGuid();
        await RunAsync(databasePath, store => store.CreateAsync(NewCreate(sessionId, "edit", "{}", createdAtUtc: 100), CancellationToken.None));
        await RunAsync(databasePath, store => store.AppendSegmentsAsync(sessionId, [NewSegment(seq: 1, startMs: 0, "doomed")], updatedAtUtc: 200, CancellationToken.None));

        var outcome = await UpdateRacingADeleteAsync(databasePath, sessionId, async other =>
        {
            await other.TranscriptSegments.Where(row => row.SessionId == sessionId).ExecuteDeleteAsync();
            await other.TranscriptionSessions.Where(row => row.Id == sessionId).ExecuteDeleteAsync();
        });

        AssertEx.Equal(TranscriptSegmentUpdateOutcome.SessionNotFound, outcome);
    }

    // Runs the delete on a second context inside the update's own save, after its tracked load: the race, made certain.
    private async Task<TranscriptSegmentUpdateOutcome> UpdateRacingADeleteAsync(string databasePath, Guid sessionId, Func<NodeChatDbContext, Task> delete)
    {
        var interceptor = new CompetingWriteInterceptor(async () =>
        {
            await using var other = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);
            await delete(other);
        });
        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder, interceptor);
        return await new TranscriptionSessionStore(context).UpdateSegmentTextAsync(sessionId, seq: 1, "edited", updatedAtUtc: 300, CancellationToken.None);
    }

    // The caller's database holds one session. Straight off the connection, bypassing the materialization interceptor,
    // so this is the ciphertext at rest.
    private static async Task<byte[]> ReadRawSegmentTextAsync(string databasePath, long seq)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM transcript_segments WHERE seq = $seq;";
        _ = command.Parameters.AddWithValue("$seq", seq);
        return await command.ExecuteScalarAsync() as byte[] ?? throw new AssertionException("Expected a non-null encrypted BLOB.");
    }

    private async Task<string> CreateSchemaAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);

        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);
        _ = await context.Database.EnsureCreatedAsync();
        return databasePath;
    }

    // One context per operation, exactly as the Scoped registration gives the service.
    private async Task RunAsync(string databasePath, Func<ITranscriptionSessionStore, Task> operation)
    {
        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);
        await operation(new TranscriptionSessionStore(context));
    }

    private async Task<T> QueryAsync<T>(string databasePath, Func<ITranscriptionSessionStore, Task<T>> operation)
    {
        await using var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);
        return await operation(new TranscriptionSessionStore(context));
    }

    private static TranscriptionSessionCreate NewCreate(Guid sessionId, string? title, string configJson, long createdAtUtc)
    {
        return new TranscriptionSessionCreate
        {
            Id = sessionId,
            Title = title,
            SourceKind = TranscriptionSourceKind.File,
            ModelId = "ggml-base.en",
            ConfigJson = configJson,
            CreatedAtUtc = createdAtUtc
        };
    }

    private static TranscriptSegmentWrite NewSegment(long seq, long startMs, string text, TranscriptChannel channel = TranscriptChannel.Mono, double? confidence = null)
    {
        return new TranscriptSegmentWrite
        {
            Seq = seq,
            StartMs = startMs,
            EndMs = startMs + 1_000,
            Text = text,
            Channel = channel,
            Confidence = confidence
        };
    }

    private static async Task<bool> DatabaseContainsAsync(string databasePath, byte[] needle)
    {
        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath);
        for (var sourceIndex = 0; sourceIndex <= fileBytes.Length - needle.Length; sourceIndex++)
        {
            if (fileBytes.AsSpan(sourceIndex, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 37)).ToArray();
    }

    /// <summary>Performs one competing write, on its own connection, inside the first save it intercepts.</summary>
    private sealed class CompetingWriteInterceptor : SaveChangesInterceptor
    {
        private readonly Func<Task> _write;
        private bool _fired;

        public CompetingWriteInterceptor(Func<Task> write)
        {
            _write = write;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await _write();
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FixedNodeSqliteKeyHolder : INodeSqliteKeyHolder
    {
        private byte[]? _key;

        public FixedNodeSqliteKeyHolder(byte[] key)
        {
            _key = key;
        }

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
