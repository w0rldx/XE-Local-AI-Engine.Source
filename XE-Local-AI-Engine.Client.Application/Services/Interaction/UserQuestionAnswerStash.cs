namespace XE_Local_AI_Engine.Client.Services.Interaction;

using System.Collections.Concurrent;

/// <summary>
///     The hand-off between the runner's human round-trip and the <c>ask_user</c> tool body, keyed on the tool call's
///     <c>CallId</c>.
///     <para>
///         WHY this exists rather than the tool simply awaiting the operator: a tool handler runs inside
///         <c>FunctionInvokingChatClient</c>, which the stream-idle watchdog wraps — a handler that blocked on a human
///         would be killed after <c>StreamIdleTimeout</c> (60 s). So <c>ask_user</c> is registered approval-required, the
///         runner performs the wait OUTSIDE the watched segment, drops the answer here, and approves the call; the
///         handler then pops the answer and returns immediately. The framework's
///         <c>FunctionInvokingChatClient.CurrentContext.CallContent.CallId</c> makes the key exact, so no ambient
///         plumbing or argument-hash matching is needed.
///     </para>
/// </summary>
public sealed class UserQuestionAnswerStash
{
    // An entry is normally popped microseconds later by the tool the runner just approved. It survives only when the
    // turn dies between the two (cancel, shutdown, provider failure), so a write-time sweep is enough to keep this
    // bounded — no timer, no background service.
    private static readonly TimeSpan StaleEntryRetention = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, StashedAnswer> _answers = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public UserQuestionAnswerStash(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    ///     Records the result the <c>ask_user</c> handler must return for <paramref name="callId" />. Overwrites any
    ///     earlier entry for the same call: the last round-trip for a call id is the authoritative one.
    /// </summary>
    public void Stash(string callId, string resultJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);

        var now = _timeProvider.GetUtcNow();
        EvictStale(now);
        _answers[callId] = new StashedAnswer(resultJson, now);
    }

    /// <summary>
    ///     Removes and returns the result for <paramref name="callId" />. Pop-once by design: a second execution of the
    ///     same call id is not a repeat of the same question, so it must fall through to the handler's fail-safe rather
    ///     than silently replay a stale answer.
    /// </summary>
    public bool TryPop(string callId, out string resultJson)
    {
        if (!string.IsNullOrEmpty(callId) && _answers.TryRemove(callId, out var stashed))
        {
            resultJson = stashed.ResultJson;
            return true;
        }

        resultJson = string.Empty;
        return false;
    }

    private void EvictStale(DateTimeOffset now)
    {
        var cutoff = now - StaleEntryRetention;
        foreach (var entry in _answers)
        {
            if (entry.Value.StashedAt < cutoff)
            {
                _ = _answers.TryRemove(entry.Key, out _);
            }
        }
    }

    private readonly record struct StashedAnswer(string ResultJson, DateTimeOffset StashedAt);
}
