namespace XE_Local_AI_Engine.Client.Services.Events;

using System.Text;

/// <summary>
///     Immutable, append-only accumulator for one streamed text channel (response OR reasoning). <see cref="Append" />
///     returns a NEW instance chained to the previous one, so an existing reference is a permanently-stable O(1) snapshot
///     that is safe to read from any thread without a lock.
///
///     <para>
///     This is what lets the hot streaming path clone an <see cref="InvocationState" /> snapshot per chunk WITHOUT
///     materializing the whole accumulated string: a clone copies this reference (O(1)) instead of calling
///     <c>ToString()</c> over the entire response every chunk (which was O(n) per chunk, i.e. O(n^2) over a turn). The
///     full string is built — and cached — only when a consumer actually reads <see cref="Value" /> (the pump's debounced
///     flush, a resume replay, or the terminal flush), so materialization happens at bounded cadence, not per token.
///     </para>
///
///     <para>
///     Because appends chain onto immutable prior nodes, consecutive snapshots share their common prefix. Once the pump
///     reads snapshot k (caching its full value on that node), building snapshot k+1 stops at that cached ancestor and
///     only re-walks the tokens appended since — so a steady flush cadence keeps each materialization close to O(delta).
///     </para>
/// </summary>
internal sealed class StreamingText
{
    /// <summary>The empty accumulator. Its materialized value is the empty string.</summary>
    public static readonly StreamingText Empty = new();

    private readonly StreamingText? _previous;
    private readonly string _chunk;

    // Cached full value, computed lazily on first read of Value. A concurrent double-compute across threads is benign:
    // every writer produces the identical string and the reference assignment is atomic (mirrors the immutable-snapshot
    // reasoning in WorkerEventDispatcher.PublishStateChanged).
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

    private string Build()
    {
        // Walk newest -> oldest collecting the uncached tail chunks, stopping at the nearest ancestor whose full value is
        // already cached; that cached prefix becomes the base so a flush re-walks only the tokens appended since the last
        // read. The walk is iterative (not recursive) so a very long chain cannot overflow the stack.
        List<string>? pending = null;
        var node = this;
        string? cachedBase = null;
        while (node is not null)
        {
            var cached = node._materialized;
            if (cached is not null)
            {
                cachedBase = cached;
                break;
            }

            (pending ??= []).Add(node._chunk);
            node = node._previous;
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
