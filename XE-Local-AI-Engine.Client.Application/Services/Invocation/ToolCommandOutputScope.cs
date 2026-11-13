namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Live output of the sandbox commands a chat turn's tool calls run, flowed as two <see cref="AsyncLocal{T}" /> slots:
///     the turn (opened by the invocation runner) and the tool call whose card the output belongs to (bound by the handler).
/// </summary>
/// <remarks>
///     The call is bound at the OUTER handler because <c>run_in_agent_home</c> runs an inner agent whose own function
///     invocation replaces <see cref="FunctionInvokingChatClient.CurrentContext" />; its inner commands must still land on
///     the outer card. No turn, or a blank call id (an id-less provider), starts nothing: the command runs unobserved.
/// </remarks>
internal static class ToolCommandOutputScope
{
    private static readonly AsyncLocal<Turn?> AmbientTurn = new();
    private static readonly AsyncLocal<string?> AmbientCallId = new();

    /// <summary>Opens the turn's scope; disposal restores the previous one, so a nested turn cannot leak outward.</summary>
    public static IDisposable Open(Guid invocationId,
        Guid messageId,
        IWorkerEventDispatcher dispatcher,
        ToolCommandStopRegistry stopRegistry,
        TimeProvider timeProvider,
        ILogger logger)
    {
        var previous = AmbientTurn.Value;
        AmbientTurn.Value = new Turn(invocationId, messageId, dispatcher, stopRegistry, timeProvider, logger);
        return new Restore<Turn?>(AmbientTurn, previous);
    }

    /// <summary>Binds the tool call the function invoker is running now, for every sandbox command started below it.</summary>
    public static IDisposable BindCurrentCall()
    {
        var previous = AmbientCallId.Value;
        AmbientCallId.Value = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId;
        return new Restore<string?>(AmbientCallId, previous);
    }

    /// <summary>
    ///     Starts the live view of one command for the bound call, registered for stop; null when there is no turn or call.
    ///     The caller sets <see cref="ToolCommandOutput.Observer" /> on the request and completes or disposes the result.
    /// </summary>
    /// <param name="workspaceRoot">The working root as the child prints it, replaced by <c>&lt;workspace&gt;</c>.</param>
    /// <param name="label">The raw executable and arguments; redacted here, or null for a tool that names no command.</param>
    public static ToolCommandOutput? StartCommand(ISandboxRuntimeProvider provider,
        SandboxHandle handle,
        string executionId,
        string? workspaceRoot,
        string? label)
    {
        var callId = AmbientCallId.Value;
        return AmbientTurn.Value is { } turn && !string.IsNullOrEmpty(callId)
            ? new ToolCommandOutput(turn, callId, provider, handle, executionId, workspaceRoot, label)
            : null;
    }

    /// <summary>The completed payload with the tail of the call's last command attached (and released), or unchanged when none ran.</summary>
    public static ToolCallLifecyclePayload WithCommandTail(ToolCallLifecyclePayload completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return AmbientTurn.Value is { } turn && turn.Tails.TryRemove(completed.ToolCallId, out var tail)
            ? completed with
            {
                OutputTail = tail.Lines,
                OutputSkippedLines = tail.SkippedLines,
                LastCommandLabel = tail.Label
            }
            : completed;
    }

    /// <summary>One turn's output plumbing, and the tail each call's last command left until its completion reads it.</summary>
    internal sealed class Turn
    {
        public Turn(Guid invocationId,
            Guid messageId,
            IWorkerEventDispatcher dispatcher,
            ToolCommandStopRegistry stopRegistry,
            TimeProvider timeProvider,
            ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(stopRegistry);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentNullException.ThrowIfNull(logger);
            InvocationId = invocationId;
            MessageId = messageId;
            Dispatcher = dispatcher;
            StopRegistry = stopRegistry;
            TimeProvider = timeProvider;
            Logger = logger;
        }

        public Guid InvocationId { get; }

        public Guid MessageId { get; }

        public IWorkerEventDispatcher Dispatcher { get; }

        public ToolCommandStopRegistry StopRegistry { get; }

        public TimeProvider TimeProvider { get; }

        public ILogger Logger { get; }

        // One entry per call with a finished command, removed when that call's completion reads it.
        public ConcurrentDictionary<string, CommandTail> Tails { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>What a finished command leaves for the persisted tool part.</summary>
    internal sealed class CommandTail
    {
        public required IReadOnlyList<NodeChatToolOutputLine> Lines { get; init; }

        public required int SkippedLines { get; init; }

        public required string? Label { get; init; }
    }

    private sealed class Restore<T> : IDisposable
    {
        private readonly T _previous;
        private readonly AsyncLocal<T> _slot;

        public Restore(AsyncLocal<T> slot, T previous)
        {
            _slot = slot;
            _previous = previous;
        }

        public void Dispose()
        {
            _slot.Value = _previous;
        }
    }
}
