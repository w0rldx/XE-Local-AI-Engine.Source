namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.LiveOutput;

/// <summary>
///     The live view of ONE sandbox command on a chat tool call's card: a start event, the redacted coalesced batches, and
///     an end event with the exit code, all reported through <see cref="IWorkerEventDispatcher.ReportToolCallOutput" />.
/// </summary>
/// <remarks>
///     Observe-only: nothing here touches the command's result. The command is registered for stop from start to end. The
///     end leaves the redacted tail for the persisted tool part (<see cref="ToolCommandOutputScope.WithCommandTail" />); after
///     a flood it also replaces the card's lines with that tail, the command's real end, which the live cap cut off.
/// </remarks>
internal sealed class ToolCommandOutput : IDisposable
{
    private const int MaxLabelChars = 300;

    // A quarter of the chat stream queue's character budget, so one flooding command cannot by itself force a reconcile.
    private static readonly LiveCommandOutputLimits Limits = new()
    {
        MaxLiveChars = 256 * 1024
    };

    private readonly string? _label;
    private readonly LiveCommandOutput _output;
    private readonly ToolCommandStopRegistration? _stopRegistration;
    private readonly string _toolCallId;
    private readonly ToolCommandOutputScope.Turn _turn;
    private int _finished;
    private int _reportFaulted;

    internal ToolCommandOutput(ToolCommandOutputScope.Turn turn,
        string toolCallId,
        ISandboxRuntimeProvider provider,
        SandboxHandle handle,
        string executionId,
        string? workspaceRoot,
        string? label)
    {
        _turn = turn;
        _toolCallId = toolCallId;
        _label = label is null ? null : RedactLabel(label, workspaceRoot);
        _output = new LiveCommandOutput(Limits, workspaceRoot, OnBatch, turn.TimeProvider, turn.Logger);
        Report([], skippedLines: 0, running: true, exitCode: null, replace: true);
        _stopRegistration = turn.StopRegistry.Register(turn.MessageId, toolCallId, provider, handle, executionId);
    }

    /// <summary>The observer to set on the command's <see cref="SandboxCommandRequest.OutputObserver" />.</summary>
    public ISandboxOutputObserver Observer => _output;

    /// <summary>The operator stopped this command; a not-completed result is then a stop, never a timeout.</summary>
    public bool StopRequested => _stopRegistration?.StopRequested == true;

    /// <summary>Ends the view with the command's result: its exit code when it finished on its own, none when stopped or timed out.</summary>
    public void Complete(SandboxCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Finish(result.Completed ? result.ExitCode : null);
    }

    /// <summary>Ends the view without an exit code when <see cref="Complete" /> was not reached (the command threw).</summary>
    public void Dispose()
    {
        Finish(exitCode: null);
    }

    private static string RedactLabel(string label, string? workspaceRoot)
    {
        var text = string.IsNullOrEmpty(workspaceRoot) ? label : label.Replace(workspaceRoot, "<workspace>", StringComparison.Ordinal);
        text = string.Concat(text.Select(static character => char.IsControl(character) ? ' ' : character));
        try
        {
            text = CredentialRedaction.Redact(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[label withheld]";
        }

        if (text.Length <= MaxLabelChars)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[MaxLabelChars - 1]) ? MaxLabelChars - 1 : MaxLabelChars;
        return string.Concat(text.AsSpan(0, end), "…");
    }

    private static List<NodeChatToolOutputLine> ToWire(IReadOnlyList<LiveCommandOutputLine> lines)
    {
        return
        [
            .. lines.Select(static line => new NodeChatToolOutputLine
            {
                Stream = line.Source == SandboxOutputSource.StandardError ? NodeChatToolOutputStreams.Stderr : NodeChatToolOutputStreams.Stdout,
                Text = line.Text
            })
        ];
    }

    private void Finish(int? exitCode)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        _stopRegistration?.Dispose();
        _output.Complete();
        var tail = ToWire(_output.GetTail());
        var skippedLines = _output.SkippedLines;
        _output.Dispose();
        _turn.Tails[_toolCallId] = new ToolCommandOutputScope.CommandTail
        {
            Lines = tail,
            SkippedLines = skippedLines,
            Label = _label
        };
        Report(skippedLines > 0 ? tail : [], skippedLines, running: false, exitCode, replace: skippedLines > 0);
    }

    // Runs on a pump or timer thread under the output's lock; the dispatcher's handlers only enqueue.
    private void OnBatch(LiveCommandOutputBatch batch)
    {
        Report(ToWire(batch.Lines), batch.SkippedLines, running: true, exitCode: null, replace: false);
    }

    private void Report(IReadOnlyList<NodeChatToolOutputLine> lines, int skippedLines, bool running, int? exitCode, bool replace)
    {
        try
        {
            _turn.Dispatcher.ReportToolCallOutput(new ToolCallOutputPayload
            {
                InvocationId = _turn.InvocationId,
                ToolCallId = _toolCallId,
                Lines = lines,
                SkippedLines = skippedLines,
                CommandLabel = _label,
                CommandRunning = running,
                CommandExitCode = exitCode,
                Replace = replace
            });
        }
        catch (Exception exception)
        {
            // A live view must never fail the tool it watches.
            if (Interlocked.Exchange(ref _reportFaulted, 1) == 0)
            {
                _turn.Logger.LogWarning(exception, "Live output of a tool command could not be reported.");
            }
        }
    }
}
