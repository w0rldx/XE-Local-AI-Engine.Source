namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     EF-backed <see cref="ITranscriptionSessionStore" />. Every write goes through
///     <see cref="NodeChatDbContext.SaveChangesAsync" /> so the node encryption interceptor encrypts the title, config,
///     error pair and segment text at rest; the status transitions load the row tracked and touch only plaintext
///     columns, so the interceptor skips the encrypted ones and their ciphertext is preserved. Scoped: one instance per
///     DI scope, matching the DbContext lifetime.
/// </summary>
public sealed class TranscriptionSessionStore(NodeChatDbContext dbContext) : ITranscriptionSessionStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task CreateAsync(TranscriptionSessionCreate create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(create);

        var entity = new TranscriptionSession
        {
            Id = create.Id,
            Title = create.Title is null ? null : Encoding.UTF8.GetBytes(create.Title),
            CreatedAtUtc = create.CreatedAtUtc,
            UpdatedAtUtc = create.CreatedAtUtc,
            Status = TranscriptionSessionStatus.Created,
            SourceKind = create.SourceKind,
            ModelId = create.ModelId,
            ConfigJson = Encoding.UTF8.GetBytes(create.ConfigJson)
        };

        _ = _dbContext.TranscriptionSessions.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptionSessionDetailView?> GetWithSegmentsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.TranscriptionSessions
                                     .AsNoTracking()
                                     .Include(session => session.Segments)
                                     .FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToDetailView(entity);
    }

    public async Task<IReadOnlyList<TranscriptionSessionSummaryView>> ListAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        // Floor the page bounds so a caller passing 0/negative still returns a sane (empty) page rather than throwing.
        // A negative limit reaches SQLite as LIMIT -1, which is "no limit" — the whole table, every title decrypted.
        var take = Math.Max(val1: 0, limit);
        var skip = Math.Max(val1: 0, offset);

        // No Include: the list never needs the transcript, and a session can hold thousands of rows. The count comes
        // back as a correlated subquery, so the page knows how long each transcript is without loading one row of it.
        var rows = await _dbContext.TranscriptionSessions
                                   .AsNoTracking()
                                   .OrderByDescending(session => session.CreatedAtUtc)
                                   .ThenByDescending(session => session.Id)
                                   .Skip(skip)
                                   .Take(take)
                                   .Select(session => new SessionCountRow(session, session.Segments.Count))
                                   .ToListAsync(cancellationToken)
                                   .ConfigureAwait(false);

        return rows.Select(static row => ToSummaryView(row.Session, row.SegmentCount)).ToArray();
    }

    public Task<int> CountAsync(CancellationToken cancellationToken)
    {
        return _dbContext.TranscriptionSessions.CountAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        // The transcript is deleted set-based rather than by loading it: the relationship declares ON DELETE CASCADE,
        // but the node connection leaves PRAGMA foreign_keys off, so the database will not enforce it and the rows
        // would orphan. Loading them to let EF cascade would decrypt every segment of a long transcript only to throw
        // the plaintext away. The two statements share one transaction so a session never survives its own transcript.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _ = await _dbContext.TranscriptSegments
                            .Where(segment => segment.SessionId == sessionId)
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);

        _ = _dbContext.TranscriptionSessions.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetStatusAsync(Guid sessionId, TranscriptionSessionStatus status, long updatedAtUtc, CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        entity.Status = status;
        entity.UpdatedAtUtc = updatedAtUtc;
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> CompleteAsync(Guid sessionId, string? detectedLanguage, long durationMs, long updatedAtUtc, CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        entity.Status = TranscriptionSessionStatus.Completed;
        entity.DetectedLanguage = detectedLanguage;
        entity.DurationMs = durationMs;
        entity.UpdatedAtUtc = updatedAtUtc;
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> FailAsync(Guid sessionId, string errorCode, string errorMessage, long updatedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        var entity = await LoadTrackedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        entity.Status = TranscriptionSessionStatus.Failed;
        entity.ErrorCode = Encoding.UTF8.GetBytes(errorCode);
        entity.ErrorMessage = Encoding.UTF8.GetBytes(errorMessage);
        entity.UpdatedAtUtc = updatedAtUtc;
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AppendSegmentsAsync(Guid sessionId, IReadOnlyList<TranscriptSegmentWrite> segments, long updatedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);

        // The session is loaded before anything is written. PRAGMA foreign_keys is off on the node connection, so an
        // unknown session id would otherwise insert orphan rows that no read path can ever reach and nothing deletes.
        var session = await LoadTrackedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        if (segments.Count == 0)
        {
            return true;
        }

        session.UpdatedAtUtc = updatedAtUtc;

        foreach (var segment in segments)
        {
            _ = _dbContext.TranscriptSegments.Add(new TranscriptSegment
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Seq = segment.Seq,
                StartMs = segment.StartMs,
                EndMs = segment.EndMs,
                Text = Encoding.UTF8.GetBytes(segment.Text),
                Channel = segment.Channel,
                Confidence = segment.Confidence
            });
        }

        // One batch, one save: the rows and the session's new updated stamp either all land or none of them do.
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Tracked load so a status-only mutation leaves the encrypted properties unmodified — the SaveChanges interceptor
    // skips an unmodified column, preserving its stored ciphertext.
    private Task<TranscriptionSession?> LoadTrackedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        return _dbContext.TranscriptionSessions.FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken);
    }

    private static TranscriptionSessionSummaryView ToSummaryView(TranscriptionSession entity, int segmentCount)
    {
        return new TranscriptionSessionSummaryView
        {
            Id = entity.Id,
            Title = entity.Title is null ? null : Encoding.UTF8.GetString(entity.Title),
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Status = entity.Status,
            SourceKind = entity.SourceKind,
            ModelId = entity.ModelId,
            DetectedLanguage = entity.DetectedLanguage,
            DurationMs = entity.DurationMs,
            SegmentCount = segmentCount
        };
    }

    // The projection target for ListAsync. The session entity travels whole so the materialization interceptor still
    // decrypts its title; the count is a correlated subquery beside it, never a loaded collection.
    private sealed record SessionCountRow(TranscriptionSession Session, int SegmentCount);

    private static TranscriptionSessionDetailView ToDetailView(TranscriptionSession entity)
    {
        return new TranscriptionSessionDetailView
        {
            Id = entity.Id,
            Title = entity.Title is null ? null : Encoding.UTF8.GetString(entity.Title),
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Status = entity.Status,
            SourceKind = entity.SourceKind,
            ModelId = entity.ModelId,
            ConfigJson = Encoding.UTF8.GetString(entity.ConfigJson),
            DetectedLanguage = entity.DetectedLanguage,
            DurationMs = entity.DurationMs,
            ErrorCode = entity.ErrorCode is null ? null : Encoding.UTF8.GetString(entity.ErrorCode),
            ErrorMessage = entity.ErrorMessage is null ? null : Encoding.UTF8.GetString(entity.ErrorMessage),
            SegmentCount = entity.Segments.Count,
            Segments = entity.Segments
                             .OrderBy(segment => segment.Seq)
                             .Select(static segment => new TranscriptSegmentView
                             {
                                 Id = segment.Id,
                                 Seq = segment.Seq,
                                 StartMs = segment.StartMs,
                                 EndMs = segment.EndMs,
                                 Text = Encoding.UTF8.GetString(segment.Text),
                                 Channel = segment.Channel,
                                 Confidence = segment.Confidence
                             })
                             .ToArray()
        };
    }
}
