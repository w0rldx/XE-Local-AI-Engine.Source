namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
///     Per-request state tracking how many consecutive invalid-argument calls each tool has made, so a looping model
///     can be cut off before it exhausts the iteration budget.
/// </summary>
/// <remarks>
///     Scoped to a single agent request, never global: in production it is anchored to the function-invocation run's
///     shared <see cref="ChatMessage" /> list, the instance the framework threads through every tool call of one
///     request, so two concurrent requests get two independent scopes and the entry is reclaimed by GC with the message
///     list. A test-only <see cref="BeginScope" /> override exercises the cap without a full chat pipeline.
/// </remarks>
internal sealed class ToolArgumentRepairScope
{
    // Keyed on the run's shared message-list instance: the framework builds one context per tool call but they all
    // reference one working list, and weak keys collect a completed request's scope with its messages.
    private static readonly ConditionalWeakTable<IList<ChatMessage>, ToolArgumentRepairScope> RunScopes = new();
    private static readonly AsyncLocal<ToolArgumentRepairScope?> ScopeOverride = new();

    private readonly ConcurrentDictionary<string, int> _consecutiveInvalid = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _disabled = new(StringComparer.Ordinal);

    /// <summary>
    ///     The scope for the in-flight request, or <see langword="null" /> outside a function-invocation run, where the
    ///     per-tool cap is simply not enforced and validation and repair still apply.
    /// </summary>
    /// <remarks>Prefers an explicit <see cref="BeginScope" /> override when one is active on the current async flow.</remarks>
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
    ///     the returned handle is disposed.
    /// </summary>
    /// <remarks>
    ///     Intended for tests and any caller driving tool invocations outside the function-invocation pipeline;
    ///     production relies on the framework anchor instead.
    /// </remarks>
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
