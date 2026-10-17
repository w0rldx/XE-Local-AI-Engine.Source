namespace XE_Local_AI_Engine.Cli.Rendering;

using System.Text.Encodings.Web;
using System.Text.Json;
using XE_Local_AI_Engine.Cli.Streaming;

/// <summary>Writes a turn as JSON lines: one <see cref="AskJsonLine" /> per event or client notice, then one <see cref="AskJsonSummary" />.</summary>
internal sealed class JsonTurnRenderer : ITurnRenderer, IDisposable
{
    /// <summary>Relaxed escaping: the lines go to a pipe or a terminal, never into HTML, so quotes and non-ASCII stay literal.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TextWriter _out;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private CliStreamEvent? _last;

    public JsonTurnRenderer(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _out = output;
    }

    public static string KindName(CliStreamEventKind kind) =>
        JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    public static AskJsonLine ToLine(CliStreamEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var source = item.Source;
        return new AskJsonLine
        {
            Type = "event",
            Kind = KindName(item.Kind),
            FrameType = source.Type,
            ConversationId = item.ConversationId,
            MessageId = item.MessageId,
            RequestId = item.RequestId,
            Status = source.Status,
            Text = item.Text,
            Reasoning = item.Reasoning,
            ToolCallId = source.ToolCallId,
            ToolName = source.ToolName,
            Arguments = source.Arguments,
            Result = source.Result,
            IsError = source.IsError,
            ApprovalRequestId = source.ApprovalRequestId,
            QuestionRequestId = source.QuestionRequestId,
            Questions = source.Questions,
            SessionScopeEligible = source.SessionScopeEligible,
            NoticeKind = source.NoticeKind,
            Error = source.Error,
            Model = source.Model,
            InputTokens = source.InputTokens,
            OutputTokens = source.OutputTokens
        };
    }

    /// <summary>Writes one JSON value as one line; also used for the error line of every <c>--json</c> command.</summary>
    public static async Task WriteLineAsync<T>(TextWriter output, T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        await output.WriteLineAsync(JsonSerializer.Serialize(value, Json).AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public async Task RenderAsync(CliStreamEvent item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _last = item;
            await WriteLineAsync(_out, ToLine(item), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClientNoticeAsync(string kind, string message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteLineAsync(_out,
                new AskJsonLine
                {
                    Type = "notice",
                    Kind = kind,
                    ConversationId = _last?.ConversationId,
                    MessageId = _last?.MessageId,
                    RequestId = _last?.RequestId,
                    Text = message
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FinishAsync(TurnOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var terminal = outcome.Terminal;
            await WriteLineAsync(_out,
                new AskJsonSummary
                {
                    Outcome = terminal is null ? "none" : KindName(terminal.Kind),
                    ExitCode = outcome.ExitCode,
                    UserCancelled = outcome.UserCancelled,
                    InteractionRequired = outcome.InteractionRequired,
                    ConversationId = _last?.ConversationId,
                    MessageId = _last?.MessageId,
                    RequestId = _last?.RequestId,
                    Text = terminal?.Text ?? string.Empty,
                    Reasoning = terminal?.Reasoning ?? string.Empty,
                    Error = terminal?.Source.Error,
                    Model = terminal?.Source.Model
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() =>
        _gate.Dispose();
}
