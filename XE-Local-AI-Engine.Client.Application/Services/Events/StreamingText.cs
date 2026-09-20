namespace XE_Local_AI_Engine.Client.Services.Events;

using System.Text;

/// <summary>Immutable, append-only accumulator for one streamed text channel (response OR reasoning).</summary>
/// <remarks>
///     <see cref="Append" /> returns a NEW instance chained to the previous one, so an existing reference is a permanently-stable O(1)
///     snapshot, safe to read from any thread without a lock. That is what lets the hot streaming path clone an
///     <see cref="InvocationState" /> snapshot per chunk WITHOUT materializing the whole accumulated string: the clone copies this reference
///     instead of calling <c>ToString()</c> over the entire response every chunk, which was O(n) per chunk — O(n^2) over a turn. The full
///     string is built only when a consumer actually reads <see cref="Value" />.
/// </remarks>
internal sealed class StreamingText
{
    /// <summary>The empty accumulator. Its materialized value is the empty string.</summary>
    public static readonly StreamingText Empty = new();

    private readonly string _chunk;

    // Cleared once this node materializes (see Value); only Build reads it, and only through Volatile.Read.
    private StreamingText? _previous;

    // Cached full value, computed lazily on first read of Value. A concurrent double-compute across threads is benign: every writer produces
    // the identical string and the reference assignment is atomic (mirroring WorkerEventDispatcher.PublishStateChanged's snapshot reasoning).
    private string? _materialized;

    private StreamingText()
    {
        _chunk = string.Empty;
        _materialized = string.Empty;
        Length = 0;
    }

    private StreamingText(StreamingText previous, string chunk)
    {
        _previous = previous;
        _chunk = chunk;
        Length = previous.Length + chunk.Length;
    }

    /// <summary>Cumulative character length of the accumulated text. O(1) and never materializes the string.</summary>
    public int Length { get; }

    /// <summary>The full accumulated string. Built once on first read and cached, so repeated reads are O(1).</summary>
    /// <remarks>
    ///     Reading also COLLAPSES the chain: this node now carries the whole prefix, so the link would otherwise pin every intermediate node —
    ///     and the full string each of them cached on an earlier read — for the lifetime of the invocation, tens of MB of dead strings on a
    ///     long turn. Dropping it is safe against a concurrent <see cref="Build" /> walk by ordering: the release-write of <c>_previous</c>
    ///     cannot move ahead of the plain write of <c>_materialized</c>, and <see cref="Build" /> reads <c>_previous</c> with a matching
    ///     <c>Volatile.Read</c>, so a walker that sees the null is guaranteed to see the value published just before it.
    /// </remarks>
    public string Value
    {
        get
        {
            var cached = _materialized;
            if (cached is not null)
            {
                return cached;
            }

            var built = Build();
            _materialized = built;

            // Collapse the chain now that this node carries the whole prefix. A concurrent Build re-reads _materialized instead of mistaking
            // the missing link for the chain's end (see Build's null-previous branch); the ordering that makes that safe is in the remarks.
            Volatile.Write(ref _previous, value: null);
            return built;
        }
    }

    /// <summary>Returns a new accumulator with <paramref name="chunk" /> appended; an empty chunk returns this instance.</summary>
    public StreamingText Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.Length == 0 ? this : new StreamingText(this, chunk);
    }

    /// <summary>Wraps an already-materialized string as an accumulator (used when copying a plain string into state).</summary>
    public static StreamingText FromString(string value)
    {
        return string.IsNullOrEmpty(value) ? Empty : Empty.Append(value);
    }

    /// <summary>Materializes the accumulated string, re-walking only the chunks appended since the last cached ancestor.</summary>
    /// <remarks>
    ///     Because appends chain onto immutable prior nodes, consecutive snapshots share their common prefix: once a node has cached its full
    ///     value, a later build stops there and only re-walks the tokens appended since, so a steady flush cadence keeps each materialization
    ///     close to O(delta). The walk is iterative, not recursive, so a very long chain cannot overflow the stack.
    /// </remarks>
    private string Build()
    {
        // Walk newest -> oldest collecting the uncached tail chunks, stopping at the nearest ancestor whose full value is already
        // cached; that cached prefix becomes the base.
        List<string>? pending = null;
        var node = this;
        string? cachedBase;
        while (true)
        {
            var cached = node._materialized;
            if (cached is not null)
            {
                cachedBase = cached;
                break;
            }

            (pending ??= []).Add(node._chunk);

            // A null link means an ancestor collapsed WHILE this walk was in flight (the Empty root materializes in its ctor, so the branch above took it).
            // This node's value is already published: re-read it as the base — a missing link read as the chain's end drops everything above — and give back the chunk it contains.
            var previous = Volatile.Read(ref node._previous);
            if (previous is null)
            {
                cachedBase = Volatile.Read(ref node._materialized);
                pending.RemoveAt(pending.Count - 1);
                break;
            }

            node = previous;
        }

        var builder = new StringBuilder(Length);
        if (!string.IsNullOrEmpty(cachedBase))
        {
            builder.Append(cachedBase);
        }

        if (pending is not null)
        {
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                builder.Append(pending[i]);
            }
        }

        return builder.ToString();
    }
}
