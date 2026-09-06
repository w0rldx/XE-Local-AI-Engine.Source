namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using System.Collections.Concurrent;

/// <summary>
///     Per-turn state for the tool-relevance offer, flowed as an <see cref="AsyncLocal{T}" /> in the same shape as
///     <c>ToolResultBudgetScope</c> and <c>ProviderCallBudget</c>: the invocation runner seeds exactly one scope when a
///     turn begins, and the send-time pipeline hop several awaited frames below reads it without a parameter on the
///     MAF/<c>IChatClient</c> surface.
///     <para>
///         <b>The slot holds a mutable holder, not a value.</b> The hop and the <c>list_tools</c> handler run below the
///         opener, and an <see cref="AsyncLocal{T}" /> assignment made in a callee never propagates back out to it — so
///         the per-array decisions and the pending notice counts live on a reference type the opener already holds.
///     </para>
///     <para>
///         The one departure from <c>ToolResultBudgetScope</c>: that scope is read-only in the callee and tighten-only,
///         while this one is WRITTEN by the callee. It widens no policy — everything it carries is tool names that were
///         already in the turn's executable list.
///     </para>
/// </summary>
public static class ToolRelevanceScope
{
    // The single ambient slot. AsyncLocal flows the holder into every continuation the turn awaits, including the
    // function-invocation pipeline's inner provider rounds and any nested sub-agent send.
    private static readonly AsyncLocal<ToolRelevanceState?> AmbientState = new();

    /// <summary>The turn's relevance state, or <see langword="null" /> when no scope was seeded (no filtering).</summary>
    internal static ToolRelevanceState? Current => AmbientState.Value;

    /// <summary>
    ///     Seeds the turn's relevance state and returns a scope whose disposal restores the prior ambient value (rather
    ///     than clearing it, so a nested seed cannot leak into an outer turn).
    /// </summary>
    /// <param name="active">
    ///     Whether filtering may engage at all. <see langword="false" /> — the shipped default — makes the hop a
    ///     reference-equality pass-through and suppresses the <c>list_tools</c> append.
    /// </param>
    /// <param name="coreNames">The node's always-on tool names, from <c>IToolRelevanceCoreSet</c>.</param>
    public static IDisposable BeginScope(bool active, IReadOnlySet<string> coreNames)
    {
        ArgumentNullException.ThrowIfNull(coreNames);

        var previous = AmbientState.Value;
        AmbientState.Value = new ToolRelevanceState
        {
            Active = active,
            CoreNames = coreNames
        };
        return new Scope(previous);
    }

    // Restores the prior ambient state when disposed. Idempotent: a double-dispose re-restores the same value.
    private sealed class Scope : IDisposable
    {
        private readonly ToolRelevanceState? _previous;

        public Scope(ToolRelevanceState? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientState.Value = _previous;
        }
    }
}

/// <summary>
///     Identity of one outbound <c>tools</c> array: a 64-bit ordinal FNV-1a hash of the name sequence, in order, AND
///     the name sequence itself. Equality compares the hash first and then the names, so two arrays that hash alike are
///     two DISTINCT keys with two distinct decisions rather than one silently shared one — a collision costs one extra
///     decision, never a wrong answer.
/// </summary>
internal readonly struct ArrayKey : IEquatable<ArrayKey>
{
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    /// <summary>Builds a key over <paramref name="names" />, hashing the sequence in order.</summary>
    public ArrayKey(IReadOnlyList<string> names)
        : this(ComputeHash(names), [.. names])
    {
    }

    /// <summary>
    ///     Builds a key with an explicit hash. Exists so a test can force two distinct name sequences to share a hash
    ///     and prove the collision path keeps two decisions.
    /// </summary>
    internal ArrayKey(ulong hash, string[] names)
    {
        Hash = hash;
        Names = names;
    }

    /// <summary>The 64-bit ordinal FNV-1a hash of the name sequence.</summary>
    public ulong Hash { get; }

    /// <summary>The name sequence itself, kept for the compare; never mutated after construction.</summary>
    public string[] Names { get; }

    public static bool operator ==(ArrayKey left, ArrayKey right) =>
        left.Equals(right);

    public static bool operator !=(ArrayKey left, ArrayKey right) =>
        !left.Equals(right);

    public bool Equals(ArrayKey other)
    {
        return Hash == other.Hash
               && (Names ?? []).AsSpan().SequenceEqual((other.Names ?? []).AsSpan(), StringComparer.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is ArrayKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Hash.GetHashCode();
    }

    private static ulong ComputeHash(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var hash = FnvOffsetBasis;
        foreach (var name in names)
        {
            foreach (var character in name ?? string.Empty)
            {
                hash = (hash ^ character) * FnvPrime;
            }

            // Separator byte, so ["ab","c"] and ["a","bc"] cannot hash alike through concatenation alone.
            hash = (hash ^ 0xFF) * FnvPrime;
        }

        return hash;
    }
}

/// <summary>
///     The decision for one tool array: which names were offered, which were held back, and which of the held-back ones
///     a <c>list_tools</c> call has since revealed. The decision object — not a key — is what the <c>list_tools</c>
///     instance is bound to, so a reveal always lands on the array the model was actually looking at.
/// </summary>
internal sealed class ArrayDecision
{
    private readonly ConcurrentDictionary<string, byte> _revealed = new(StringComparer.Ordinal);

    public required IReadOnlyList<string> OfferedNames { get; init; }

    public required IReadOnlyList<string> HiddenNames { get; init; }

    /// <summary>Lock-free union; idempotent, so a model calling <c>list_tools</c> twice reveals the same set.</summary>
    public void Reveal(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        foreach (var name in names)
        {
            _revealed[name] = 0;
        }
    }

    public bool IsRevealed(string name)
    {
        return _revealed.ContainsKey(name);
    }
}

/// <summary>
///     The mutable holder in the ambient slot. One per turn; the per-array decisions under it are computed exactly once
///     each and reused across the turn's rounds, so a tool-calling loop pays one selection rather than one per round —
///     which is also what keeps the prompt prefix and the compiled GBNF grammar stable within the turn.
/// </summary>
internal sealed class ToolRelevanceState
{
    private readonly ConcurrentDictionary<ArrayKey, Lazy<Task<ArrayDecision>>> _byArray = new();

    /// <summary>
    ///     Hidden-tool count awaiting the runner's notice drain, from the most recently computed decision.
    ///     <see cref="Interlocked" />-exchanged inside the single-flight factory, so it always describes ONE array.
    /// </summary>
    public int PendingNoticeHiddenCount;

    /// <summary>
    ///     Eligible-tool total for the same decision as <see cref="PendingNoticeHiddenCount" />, so the drained pair is
    ///     always the "N of M" of a single array rather than a sum across two.
    /// </summary>
    public int PendingNoticeTotalCount;

    public required bool Active { get; init; }

    public required IReadOnlySet<string> CoreNames { get; init; }

    /// <summary>
    ///     Whether this array already has a decision (computed or in flight). The send-time hop asks before deciding
    ///     whether it still needs a relevance query: one decision per array per turn means a round that carries no
    ///     text of its own - an approval resume - must reuse the decision rather than fall through to the full array.
    /// </summary>
    public bool HasDecision(ArrayKey key)
    {
        return _byArray.ContainsKey(key);
    }

    /// <summary>
    ///     Returns this array's decision, running <paramref name="factory" /> at most once per distinct key even under
    ///     concurrent callers — <see cref="LazyThreadSafetyMode.ExecutionAndPublication" /> is exactly-once by contract,
    ///     so every caller after the first awaits the same task.
    ///     <para>
    ///         <b>The shared computation takes no caller token.</b> Under a shared <see cref="Lazy{T}" /> the FIRST
    ///         caller's token would be the one the work runs under, so that caller cancelling — an idle-watchdog expiry,
    ///         a user stop, a pre-first-token retry abandoning attempt 1 — would cancel the work every other waiter is
    ///         awaiting, for a reason that had nothing to do with them, and which one won would depend on who raced in
    ///         first. <paramref name="callerToken" /> therefore aborts only THIS caller's wait; the shared task keeps
    ///         running for the others, bounded by whatever bound the selector applies to itself.
    ///     </para>
    ///     <para>
    ///         Eviction is likewise keyed to the SHARED task's own terminal state - faulted OR cancelled - and never
    ///         to a caller's cancelled wait: after a caller's own wait aborts the shared task is still running, so the
    ///         entry is still valid and the next caller (including the pre-first-token retry re-invoking the whole
    ///         send factory) must reuse it rather than pay a second selection.
    ///     </para>
    /// </summary>
    public async Task<ArrayDecision> GetOrComputeAsync(ArrayKey key, Func<Task<ArrayDecision>> factory, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var lazy = _byArray.GetOrAdd(key, _ => new Lazy<Task<ArrayDecision>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));

        // GetOrAdd may build more than one Lazy under contention, but only the published one is ever materialized here,
        // so the factory still runs exactly once per key.
        var shared = lazy.Value;

        // Eviction cannot depend on a waiter being present. If the only waiter's own wait aborts and the shared task
        // LATER ends faulted or cancelled with nobody awaiting, the finally below never runs for it and the next
        // caller would be handed the dead task and degrade to the unfiltered offer for free. The continuation hangs
        // off the shared task itself, so it fires either way; the finally stays as the belt. Attaching one per caller
        // is harmless - TryRemove is value-comparing and idempotent - and observing Exception here keeps an unawaited
        // fault off TaskScheduler.UnobservedTaskException.
        _ = shared.ContinueWith(task =>
            {
                _ = task.Exception;
                _ = _byArray.TryRemove(KeyValuePair.Create(key, lazy));
            },
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            return await shared.WaitAsync(callerToken).ConfigureAwait(false);
        }
        finally
        {
            // TERMINAL-but-unsuccessful, not merely faulted: a shared computation that ends Canceled (an
            // OperationCanceledException escaping the selector - an HttpClient timeout throws TaskCanceledException,
            // a subclass) is not IsFaulted, and caching it would poison the key for the rest of the turn. An
            // INCOMPLETE task fails IsCompleted, so a caller's own cancelled wait still evicts nothing.
            if (shared.IsCompleted && !shared.IsCompletedSuccessfully)
            {
                // Value-comparing overload, so a racing recompute that already replaced this entry is not clobbered.
                _ = _byArray.TryRemove(KeyValuePair.Create(key, lazy));
            }
        }
    }
}
