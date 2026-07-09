namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
///     Per-request state that tracks how many consecutive invalid-argument calls each tool has made so a looping model
///     can be cut off before it exhausts the iteration budget. State is scoped to a single agent request, never global:
///     in production it is anchored to the function-invocation run's shared <see cref="ChatMessage" /> list (the same
///     instance the framework threads through every tool call of one request), so two concurrent requests get two
///     independent scopes and the entry is reclaimed by GC when the request's message list is. A test-only
///     <see cref="BeginScope" /> override lets the cap be exercised without driving a full chat pipeline.
/// </summary>
internal sealed class ToolArgumentRepairScope
{
    // Keyed on the run's shared message-list instance. The framework builds one FunctionInvocationContext per tool call
    // but they all reference the same working-message list for the request, so it is a stable per-request anchor. Weak
    // keys mean a completed request's scope is collected with its messages — no manual cleanup, no leak.
    private static readonly ConditionalWeakTable<IList<ChatMessage>, ToolArgumentRepairScope> RunScopes = new();
    private static readonly AsyncLocal<ToolArgumentRepairScope?> ScopeOverride = new();

    private readonly ConcurrentDictionary<string, int> _consecutiveInvalid = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _disabled = new(StringComparer.Ordinal);

    /// <summary>
    ///     The scope for the in-flight request, or <see langword="null" /> when the caller is not inside a
    ///     function-invocation run (in which case the per-tool cap is simply not enforced — validation and repair still
    ///     apply). Prefers an explicit <see cref="BeginScope" /> override when one is active on the current async flow.
    /// </summary>
    public static ToolArgumentRepairScope? Current
    {
        get
        {
            if (ScopeOverride.Value is { } overridden)
            {
                return overridden;
            }

            var messages = FunctionInvokingChatClient.CurrentContext?.Messages;
            return messages is null ? null : RunScopes.GetValue(messages, static _ => new ToolArgumentRepairScope());
        }
    }

    /// <summary>
    ///     Establishes an explicit scope for the current async flow, overriding the framework-anchored resolution until
    ///     the returned handle is disposed. Intended for tests and any caller that drives tool invocations outside the
    ///     function-invocation pipeline; production relies on the framework anchor instead.
    /// </summary>
    public static IDisposable BeginScope()
    {
        ScopeOverride.Value = new ToolArgumentRepairScope();
        return new ScopeReleaser();
    }

    /// <summary>Whether the tool has already been disabled for the remainder of this request.</summary>
    public bool IsDisabled(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return _disabled.ContainsKey(toolName);
    }

    /// <summary>Records one more consecutive invalid call for the tool and returns the running count.</summary>
    public int RecordInvalidCall(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return _consecutiveInvalid.AddOrUpdate(toolName, 1, static (_, count) => count + 1);
    }

    /// <summary>Clears the consecutive-invalid streak for the tool after a call that validated and executed.</summary>
    public void RecordValidCall(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        _ = _consecutiveInvalid.TryRemove(toolName, out _);
    }

    /// <summary>Marks the tool as disabled for the remainder of this request.</summary>
    public void Disable(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        _ = _disabled.TryAdd(toolName, 0);
    }

    private sealed class ScopeReleaser : IDisposable
    {
        public void Dispose()
        {
            ScopeOverride.Value = null;
        }
    }
}
