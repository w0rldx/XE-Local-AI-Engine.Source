namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     The in-memory content index behind the conversation search: each chat conversation's decrypted title and
///     message text, held in process memory only and never written anywhere.
/// </summary>
/// <remarks>
///     <see cref="NodeChatPersistenceWriter" /> owns it and marks a conversation stale after every write it runs; a
///     search re-reads the stale ones first, so a result never lags a write that has returned. The start-up backfill in
///     <see cref="ConversationSearchBackfillService" /> fills in everything stored before the process started. Reads
///     go through <see cref="NodeChatReadModel" />; this type holds no database access of its own.
/// </remarks>
internal sealed class ConversationSearchIndex
{
    /// <summary>Indexed message text per conversation, the NEWEST text kept; older text beyond it is not searchable.</summary>
    /// <remarks>
    ///     The memory ceiling is this many UTF-16 chars (about 400 KB) times the number of chat conversations, plus
    ///     their titles. <c>NodeMetrics.ChatSearchIndexedChars</c> reports what is actually held.
    /// </remarks>
    internal const int MaxIndexedCharsPerConversation = 200_000;

    /// <summary>Indexed chars past which the index logs a warning: 250 million UTF-16 chars, about 500 MB of process memory.</summary>
    /// <remarks>A warning, never a cap: capping would make search miss text that is stored, against the always-current rule.</remarks>
    internal const long IndexedCharsWarningThreshold = 250_000_000;

    private const int SnippetCharsBefore = 40;
    private const int SnippetCharsAfter = 80;

    private readonly ConcurrentDictionary<Guid, ConversationSearchDocument> _documents = new();
    private readonly ConcurrentDictionary<Guid, long> _stale = new();
    private readonly Lock _storeSync = new();
    private readonly ILogger? _logger;
    private readonly long _warningThresholdChars;
    private int _backfillComplete;
    private long _indexedChars;
    private long _writeStamp;

    /// <param name="logger">Receives the threshold warning; null logs nothing.</param>
    /// <param name="warningThresholdChars">Overrides <see cref="IndexedCharsWarningThreshold" />, for tests.</param>
    internal ConversationSearchIndex(ILogger? logger = null, long warningThresholdChars = IndexedCharsWarningThreshold)
    {
        _logger = logger;
        _warningThresholdChars = warningThresholdChars;
    }

    /// <summary>Serializes every read-then-store pass (backfill batches and stale refreshes), so an older read never replaces a newer one.</summary>
    internal SemaphoreSlim RefreshGate { get; } = new(initialCount: 1, maxCount: 1);

    /// <summary>A TEST-ONLY seam, null in production: runs after a search refresh stored a conversation and before it clears that mark.</summary>
    internal Func<Guid, Task>? AfterRefreshStore { get; set; }

    internal bool IsBackfillComplete => Volatile.Read(ref _backfillComplete) == 1;

    internal int Count => _documents.Count;

    internal void MarkBackfillComplete()
    {
        Volatile.Write(ref _backfillComplete, 1);
    }

    /// <summary>Flags a conversation for a re-read before the next search. Each mark carries a fresh stamp.</summary>
    internal void MarkStale(Guid conversationId)
    {
        // Guid.Empty is the writer's cross-conversation key (the start-up recovery pass), which runs before the backfill reads anything.
        if (conversationId != Guid.Empty)
        {
            _stale[conversationId] = Interlocked.Increment(ref _writeStamp);
        }
    }

    internal KeyValuePair<Guid, long>[] SnapshotStale()
    {
        return _stale.ToArray();
    }

    /// <summary>Clears a stale mark only if no later write re-marked the conversation while it was being read.</summary>
    internal void ClearStale(Guid conversationId, long stamp)
    {
        _ = _stale.TryRemove(new KeyValuePair<Guid, long>(conversationId, stamp));
    }

    internal bool Contains(Guid conversationId)
    {
        return _documents.ContainsKey(conversationId);
    }

    /// <summary>Stores the conversation's current document, or drops it when the read found no searchable chat.</summary>
    internal void Store(Guid conversationId, ConversationSearchDocument? document)
    {
        // Not under RefreshGate when a delete drops its entry eagerly, so the swap and the metric delta share one lock.
        lock (_storeSync)
        {
            _ = _documents.TryGetValue(conversationId, out var previous);
            if (document is null)
            {
                _ = _documents.TryRemove(conversationId, out _);
            }
            else
            {
                _documents[conversationId] = document;
            }

            var conversationDelta = (document is null ? 0 : 1) - (previous is null ? 0 : 1);
            var charDelta = IndexedChars(document) - IndexedChars(previous);
            if (conversationDelta != 0)
            {
                NodeMetrics.ChatSearchIndexedConversations.Add(conversationDelta);
            }

            if (charDelta != 0)
            {
                NodeMetrics.ChatSearchIndexedChars.Add(charDelta);
                var before = _indexedChars;
                _indexedChars += charDelta;
                TrackThreshold(before, _indexedChars);
            }
        }
    }

    // Under _storeSync. Warns once per upward crossing; dropping back below re-arms it.
    private void TrackThreshold(long before, long after)
    {
        var wasOver = before >= _warningThresholdChars;
        var isOver = after >= _warningThresholdChars;
        if (wasOver == isOver)
        {
            return;
        }

        NodeMetrics.ChatSearchIndexedCharsOverThreshold.Add(isOver ? 1 : -1);
        if (isOver)
        {
            _logger?.LogWarning(
                "The conversation search index holds {IndexedChars} chars of decrypted text (about {IndexedMegabytes} MB), past its {ThresholdChars}-char warning threshold. It keeps indexing; the node's memory grows with the chat history.",
                after,
                after * 2 / 1_000_000,
                _warningThresholdChars);
        }
    }

    internal IReadOnlyList<NodeChatConversationSearchHitDto> Search(string query, bool includeArchived, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0 || limit <= 0)
        {
            return [];
        }

        return _documents.Where(entry => (includeArchived || !entry.Value.Archived) && MatchesAll(entry.Value, terms))
                         .OrderByDescending(static entry => entry.Value.LastSeenUtc)
                         .Take(limit)
                         .Select(entry => ToHit(entry.Key, entry.Value, terms))
                         .ToArray();
    }

    /// <summary>
    ///     Joins message texts given NEWEST first into chronological order, keeping the newest
    ///     <see cref="MaxIndexedCharsPerConversation" /> chars: the oldest kept message loses its head.
    /// </summary>
    internal static string JoinContent(IEnumerable<string> newestFirstTexts)
    {
        ArgumentNullException.ThrowIfNull(newestFirstTexts);

        var kept = new List<string>();
        var length = 0;
        foreach (var text in newestFirstTexts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var room = MaxIndexedCharsPerConversation - length - (kept.Count > 0 ? 1 : 0);
            if (room <= 0)
            {
                break;
            }

            var tailStart = Math.Max(0, text.Length - room);
            if (tailStart > 0 && char.IsLowSurrogate(text[tailStart]))
            {
                tailStart++;
            }

            kept.Add(text[tailStart..]);
            length += kept[^1].Length + (kept.Count > 1 ? 1 : 0);
        }

        kept.Reverse();
        return string.Join('\n', kept);
    }

    private static long IndexedChars(ConversationSearchDocument? document)
    {
        return document is null ? 0 : document.Content.Length + (document.Title?.Length ?? 0);
    }

    private static bool MatchesAll(ConversationSearchDocument document, string[] terms)
    {
        return terms.All(term => (document.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                                 || document.Content.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static NodeChatConversationSearchHitDto ToHit(Guid conversationId, ConversationSearchDocument document, string[] terms)
    {
        var titleHit = document.Title?.Contains(terms[0], StringComparison.OrdinalIgnoreCase) ?? false;
        var contentHit = terms.Select(term => document.Content.IndexOf(term, StringComparison.OrdinalIgnoreCase))
                              .Where(static index => index >= 0)
                              .DefaultIfEmpty(-1)
                              .First();

        return new NodeChatConversationSearchHitDto
        {
            ConversationId = conversationId,
            Title = document.Title,
            Snippet = contentHit < 0 ? null : Snippet(document.Content, contentHit),
            MatchedIn = titleHit || contentHit < 0 ? NodeChatSearchMatchValues.Title : NodeChatSearchMatchValues.Content,
            CreatedAtUtc = document.CreatedAtUtc,
            LastSeenUtc = document.LastSeenUtc,
            Origin = document.Origin,
            IsPinned = document.IsPinned,
            Archived = document.Archived
        };
    }

    private static string Snippet(string content, int hitIndex)
    {
        var start = Math.Max(0, hitIndex - SnippetCharsBefore);
        var end = Math.Min(content.Length, hitIndex + SnippetCharsAfter);
        // Never cut inside a surrogate pair: widen either edge by the pair's other half.
        if (start > 0 && char.IsLowSurrogate(content[start]))
        {
            start--;
        }

        if (end < content.Length && char.IsLowSurrogate(content[end]))
        {
            end++;
        }

        var excerpt = string.Join(' ', content[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (start > 0 ? "…" : string.Empty) + excerpt + (end < content.Length ? "…" : string.Empty);
    }
}
