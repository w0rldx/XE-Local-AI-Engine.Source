namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The teardown deadlines <see cref="InProcMcpServer" /> hit, per test, so an expiry a passing body never consumed fails that test
///     instead of being swallowed by <c>await using</c>.
/// </summary>
/// <remarks>
///     Keyed by <c>TestContext.Current</c>, an async-local that flows into the test body's disposals, so no fixture user threads a log
///     through. An <c>[After(Test)]</c> hook in each fixture-using class calls <see cref="ThrowIfUnconsumed" />.
/// </remarks>
internal static class McpTeardownLog
{
    private static readonly ConcurrentDictionary<object, ConcurrentQueue<string>> Expiries = new();

    /// <summary>The scope a disposal records under: the running test, or a shared bucket outside any test.</summary>
    public static object CurrentScope => (object?)TestContext.Current ?? typeof(McpTeardownLog);

    public static void Record(object scope, string server, string step)
    {
        Expiries.GetOrAdd(scope, static _ => new ConcurrentQueue<string>()).Enqueue($"{server} (stuck at {step})");
    }

    /// <summary>Consumes one recorded expiry, for a test that expects its teardown to hit the deadline.</summary>
    public static bool TryConsume(object scope)
    {
        return Expiries.TryGetValue(scope, out var expiries) && expiries.TryDequeue(out _);
    }

    /// <summary>Forgets the scope, then fails when an expiry was left unconsumed, unless the body already failed with its own assertion.</summary>
    public static void ThrowIfUnconsumed(object scope, bool bodyFailed)
    {
        if (!Expiries.TryRemove(scope, out var expiries) || expiries.IsEmpty || bodyFailed)
        {
            return;
        }

        throw new AssertionException($"In-process MCP server teardown hit its deadline: {string.Join(", ", expiries)}.");
    }
}
