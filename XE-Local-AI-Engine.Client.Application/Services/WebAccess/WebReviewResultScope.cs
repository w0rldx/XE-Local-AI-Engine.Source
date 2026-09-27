namespace XE_Local_AI_Engine.Client.Services.WebAccess;

using System.Collections.Concurrent;

/// <summary>
///     The per-invocation hand-off between the result review gate and the web tool handlers, keyed by tool-call id.
/// </summary>
/// <remarks>
///     An <see cref="AsyncLocal{T}" /> holding a mutable dictionary, as <c>ToolRelevanceScope</c> does: the runner opens one
///     scope per agent stream, the coordinator stashes reviewed results in it, and a handler reads only its own
///     invocation's entries. No scope, a blank id or a missing entry is a miss, and the handler refuses.
/// </remarks>
internal static class WebReviewResultScope
{
    private static readonly AsyncLocal<ConcurrentDictionary<string, string>?> Ambient = new();

    public static IDisposable BeginScope()
    {
        var previous = Ambient.Value;
        Ambient.Value = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        return new Scope(previous);
    }

    /// <summary>Records what the handler for <paramref name="callId" /> returns; ignored outside a scope or for a blank id.</summary>
    public static void Stash(string? callId, string resultJson)
    {
        if (!string.IsNullOrEmpty(callId) && Ambient.Value is { } results)
        {
            results[callId] = resultJson;
        }
    }

    /// <summary>Removes and returns the reviewed result for <paramref name="callId" />; pop-once, like the <c>ask_user</c> stash.</summary>
    public static bool TryPop(string? callId, out string resultJson)
    {
        if (!string.IsNullOrEmpty(callId) && Ambient.Value is { } results && results.TryRemove(callId, out var stashed))
        {
            resultJson = stashed;
            return true;
        }

        resultJson = string.Empty;
        return false;
    }

    private sealed class Scope : IDisposable
    {
        private readonly ConcurrentDictionary<string, string>? _previous;

        public Scope(ConcurrentDictionary<string, string>? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Ambient.Value = _previous;
        }
    }
}
