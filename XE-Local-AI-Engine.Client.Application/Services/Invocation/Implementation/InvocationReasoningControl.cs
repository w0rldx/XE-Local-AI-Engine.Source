namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The outcome of an "Answer now" request.</summary>
public enum ReasoningEndOutcome
{
    /// <summary>llama-server closed the reasoning block; the answer streams next.</summary>
    Ended,

    /// <summary>No turn for that message is running on this node.</summary>
    NotFound,

    /// <summary>The turn is running but is not reasoning right now, or its completion was not armed for control.</summary>
    NotReasoning,

    /// <summary>llama-server refused or could not be reached.</summary>
    Rejected
}

/// <summary>
///     "Answer now": ends the reasoning block of a running llama.cpp turn without stopping the turn, through
///     llama-server's realtime reasoning control.
/// </summary>
/// <remarks>
///     The runner tracks each turn by its assistant message id (<see cref="Track" />), arms it with the completion id and
///     server address the armed stream advertised (<see cref="LlamaServerReasoningControl" />), and flips the reasoning
///     flag as reasoning and answer text arrive; the endpoint then calls <see cref="EndReasoningAsync" />. One entry per
///     live turn, removed when the turn ends. A tool loop is several completions: each one re-arms with its own id.
/// </remarks>
// Not interfaced: one implementation, and the state is the runner's own. A singleton because the request to end the
// reasoning arrives on a different call stack from the turn it targets, like the cancellation registry it mirrors.
public sealed partial class InvocationReasoningControl
{
    private readonly ConcurrentDictionary<Guid, Turn> _turns = new();
    private readonly ILlamaServerNativeClient _nativeClient;
    private readonly ILogger<InvocationReasoningControl> _logger;

    public InvocationReasoningControl(ILlamaServerNativeClient nativeClient, ILogger<InvocationReasoningControl> logger)
    {
        _nativeClient = nativeClient ?? throw new ArgumentNullException(nameof(nativeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Starts tracking the turn writing <paramref name="messageId" />; disposing the handle stops it.</summary>
    public Turn Track(Guid messageId)
    {
        var turn = new Turn(this, messageId);
        _turns[messageId] = turn;
        return turn;
    }

    /// <summary>Asks llama-server to end the current reasoning block of the turn writing <paramref name="messageId" />.</summary>
    public async Task<ReasoningEndOutcome> EndReasoningAsync(Guid messageId, CancellationToken ct)
    {
        if (!_turns.TryGetValue(messageId, out var turn))
        {
            return ReasoningEndOutcome.NotFound;
        }

        if (turn.Snapshot() is not { Reasoning: true } armed)
        {
            return ReasoningEndOutcome.NotReasoning;
        }

        LlamaServerReasoningControlResult result;
        try
        {
            result = await _nativeClient.EndReasoningAsync(armed.BaseAddress, armed.CompletionId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogControlUnreachable(ex, messageId);
            return ReasoningEndOutcome.Rejected;
        }

        if (!result.Success)
        {
            LogControlRefused(messageId, result.Message);
            return ReasoningEndOutcome.Rejected;
        }

        LogReasoningEnded(messageId, armed.CompletionId);
        return ReasoningEndOutcome.Ended;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Answer now: ended the reasoning of message {MessageId} (completion {CompletionId}).")]
    private partial void LogReasoningEnded(Guid messageId, string completionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Answer now: llama-server refused to end the reasoning of message {MessageId}: {ServerMessage}")]
    private partial void LogControlRefused(Guid messageId, string? serverMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Answer now: llama-server could not be reached for message {MessageId}.")]
    private partial void LogControlUnreachable(Exception exception, Guid messageId);

    internal sealed record ArmedCompletion
    {
        public required Uri BaseAddress { get; init; }

        public required string CompletionId { get; init; }

        public bool Reasoning { get; init; }
    }

    /// <summary>The runner's handle on one tracked turn. Written only by the turn's own stream loop.</summary>
    public sealed class Turn : IDisposable
    {
        private readonly InvocationReasoningControl _owner;
        private readonly Guid _messageId;
        private ArmedCompletion? _armed;

        internal Turn(InvocationReasoningControl owner, Guid messageId)
        {
            _owner = owner;
            _messageId = messageId;
        }

        /// <summary>Folds one streamed update: arms on an advertised completion, then tracks whether it is reasoning.</summary>
        /// <param name="rawRepresentation">The update's raw representation (the MEAI update under the agent update).</param>
        /// <param name="sawReasoning">The update carried reasoning text.</param>
        /// <param name="sawAnswer">The update carried answer text or a tool call, which ends the reasoning phase.</param>
        public void Observe(object? rawRepresentation, bool sawReasoning, bool sawAnswer)
        {
            var current = Volatile.Read(ref _armed);
            if (LlamaServerReasoningControl.TryRead(rawRepresentation, out var baseAddress, out var completionId))
            {
                current = new ArmedCompletion
                {
                    BaseAddress = baseAddress,
                    CompletionId = completionId
                };
            }

            if (current is null)
            {
                return;
            }

            var reasoning = !sawAnswer && (sawReasoning || current.Reasoning);
            if (reasoning != current.Reasoning)
            {
                current = current with { Reasoning = reasoning };
            }

            Volatile.Write(ref _armed, current);
        }

        internal ArmedCompletion? Snapshot() => Volatile.Read(ref _armed);

        public void Dispose() => _owner._turns.TryRemove(new KeyValuePair<Guid, Turn>(_messageId, this));
    }
}
