namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Events;

internal sealed class BenchmarkInvocationCapture : IDisposable
{
    private readonly Guid _runId;
    private readonly Guid _invocationId;
    private readonly IWorkerEventDispatcher _dispatcher;
    private readonly IBenchmarkEventBuffer _events;
    private readonly Lock _gate = new();
    private readonly List<BenchmarkOutputPart> _parts = [];
    private int _contentLength;
    private int _reasoningLength;

    public BenchmarkInvocationCapture(Guid runId,
        Guid invocationId,
        IWorkerEventDispatcher dispatcher,
        IBenchmarkEventBuffer events)
    {
        _runId = runId;
        _invocationId = invocationId;
        _dispatcher = dispatcher;
        _events = events;
        dispatcher.InvocationStateChanged += OnInvocationStateChanged;
        dispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;
    }

    public InvocationState? TerminalState { get; private set; }

    public IReadOnlyList<BenchmarkOutputPart> Parts
    {
        get
        {
            lock (_gate)
            {
                return _parts.ToArray();
            }
        }
    }

    public void Dispose()
    {
        _dispatcher.InvocationStateChanged -= OnInvocationStateChanged;
        _dispatcher.ToolCallLifecycleChanged -= OnToolCallLifecycleChanged;
    }

    private void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        var state = args.State;
        if (state.InvocationId != _invocationId)
        {
            return;
        }

        lock (_gate)
        {
            // Only a reclassification shrinks the answer (IWorkerEventDispatcher.ReportInvocationTextReclassifiedAsync): text it moved to the reasoning must leave the graded output.
            if (state.StreamedContent.Length < _contentLength)
            {
                _contentLength = TrimOutputTo(state.StreamedContent);
                // Subscribers already hold the cut text, and the deltas below only append.
                _events.Append(_runId, BenchmarkRunStreamEventKind.OutputTrimmed, new BenchmarkRunStreamPayload
                {
                    Content = state.StreamedContent[.._contentLength]
                });
            }

            // Reasoning first: in the one event carrying both, the reclassification, the moved text preceded the answer that replaces it.
            AppendTextDelta(state.StreamedThinkingContent,
                ref _reasoningLength,
                BenchmarkOutputParts.ReasoningKind,
                BenchmarkRunStreamEventKind.ReasoningDelta);
            AppendTextDelta(state.StreamedContent, ref _contentLength, BenchmarkOutputParts.OutputKind, BenchmarkRunStreamEventKind.OutputDelta);
            if (state.Status is InvocationStatus.Completed or InvocationStatus.Failed or InvocationStatus.Cancelled)
            {
                TerminalState = state;
            }
        }
    }

    /// <summary>Cuts the captured output parts back to their longest prefix of <paramref name="content" />; returns its length.</summary>
    /// <remarks>Earlier rounds keep their text and every reasoning and tool part keeps its place; the caller appends the remainder.</remarks>
    private int TrimOutputTo(string content)
    {
        var kept = 0;
        var diverged = false;
        List<BenchmarkOutputPart> trimmed = new(_parts.Count);
        foreach (var part in _parts)
        {
            if (!string.Equals(part.Kind, BenchmarkOutputParts.OutputKind, StringComparison.Ordinal))
            {
                trimmed.Add(part);
                continue;
            }

            var text = part.Content ?? string.Empty;
            var same = diverged ? 0 : text.AsSpan().CommonPrefixLength(content.AsSpan(kept));
            kept += same;
            diverged |= same < text.Length;
            if (same > 0)
            {
                trimmed.Add(same == text.Length
                    ? part
                    : part with
                    {
                        Content = text[..same]
                    });
            }
        }

        _parts.Clear();
        _parts.AddRange(trimmed);
        return kept;
    }

    private void AppendTextDelta(string current,
        ref int priorLength,
        string partKind,
        BenchmarkRunStreamEventKind eventKind)
    {
        if (current.Length <= priorLength)
        {
            priorLength = current.Length;
            return;
        }

        var delta = current[priorLength..];
        priorLength = current.Length;
        _parts.Add(new BenchmarkOutputPart(partKind, Content: delta));
        _events.Append(_runId, eventKind, new BenchmarkRunStreamPayload
        {
            Content = delta
        });
    }

    private void OnToolCallLifecycleChanged(object? sender, ToolCallLifecycleChangedEventArgs args)
    {
        var payload = args.Payload;
        if (payload.InvocationId != _invocationId)
        {
            return;
        }

        lock (_gate)
        {
            var requested = payload.Phase == ToolCallLifecyclePhase.Requested;
            _parts.Add(new BenchmarkOutputPart(requested ? BenchmarkOutputParts.ToolCallKind : BenchmarkOutputParts.ToolResultKind,
                ToolCallId: payload.ToolCallId,
                ToolName: payload.ToolName,
                Arguments: requested ? payload.Arguments : null,
                Result: requested ? null : payload.Result,
                IsError: requested ? null : payload.IsError,
                DurationMs: requested ? null : payload.DurationMs));
            _events.Append(_runId,
                requested ? BenchmarkRunStreamEventKind.ToolCall : BenchmarkRunStreamEventKind.ToolResult,
                new BenchmarkRunStreamPayload
                {
                    ToolCallId = payload.ToolCallId,
                    ToolName = payload.ToolName,
                    Arguments = requested ? payload.Arguments : null,
                    Result = requested ? null : payload.Result,
                    IsError = requested ? null : payload.IsError
                });
        }
    }
}
