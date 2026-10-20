namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     Per-invocation execution time of each tool call, keyed by call id, flowed as an <see cref="AsyncLocal{T}" /> in
///     the same shape as <c>ToolRelevanceScope</c>.
/// </summary>
/// <remarks>
///     The function invoker set in <c>AgentServiceCollectionExtensions.DecorateChatClientPipeline</c> records how long
///     the tool itself ran; the invocation runner opens one scope per agent stream and reads the value when it reports
///     the call completed. The slot holds a mutable dictionary because an assignment made in a callee never reaches the
///     caller. No scope or a blank id records nothing, so a call without a measurement reads as null, never 0.
/// </remarks>
public static class ToolExecutionTimingScope
{
    private static readonly AsyncLocal<ConcurrentDictionary<string, long>?> Ambient = new();

    /// <summary>Opens a fresh scope; disposal restores the previous one, so a nested scope cannot leak outward.</summary>
    public static IDisposable Open()
    {
        var previous = Ambient.Value;
        Ambient.Value = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        return new Scope(previous);
    }

    /// <summary>Records the execution time of <paramref name="callId" />; ignored outside a scope or for a blank id.</summary>
    public static void Record(string? callId, long elapsedMs)
    {
        if (!string.IsNullOrEmpty(callId) && Ambient.Value is { } durations)
        {
            durations[callId] = elapsedMs;
        }
    }

    /// <summary>The recorded execution time of <paramref name="callId" />, or <see langword="null" /> when none was recorded.</summary>
    public static long? TryGet(string? callId)
    {
        return !string.IsNullOrEmpty(callId) && Ambient.Value is { } durations && durations.TryGetValue(callId, out var elapsedMs)
            ? elapsedMs
            : null;
    }

    private sealed class Scope : IDisposable
    {
        private readonly ConcurrentDictionary<string, long>? _previous;

        public Scope(ConcurrentDictionary<string, long>? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Ambient.Value = _previous;
        }
    }
}
