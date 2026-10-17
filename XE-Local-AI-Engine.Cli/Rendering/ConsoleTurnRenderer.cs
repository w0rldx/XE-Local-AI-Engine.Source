namespace XE_Local_AI_Engine.Cli.Rendering;

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using XE_Local_AI_Engine.Cli.Commands;
using XE_Local_AI_Engine.Cli.Streaming;

/// <summary>
///     Shows one turn to a person: the answer streams to <c>content</c> as plain text, everything else (thinking, tool
///     activity, prompts announced, notices, the outcome) goes to <c>side</c> as short marked-up lines.
/// </summary>
/// <remarks>
///     <c>xe ask</c> passes standard output as content and the standard-error console as side, so a pipe receives the
///     answer alone; <c>xe chat</c> passes the same terminal for both. The answer is never sent through Spectre, which
///     would wrap it at the console width.
/// </remarks>
internal sealed class ConsoleTurnRenderer : ITurnRenderer, IDisposable
{
    private const int MaxArgumentsChars = 240;

    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TextWriter _content;
    private readonly IAnsiConsole _side;
    private readonly bool _sameScreen;
    private readonly bool _showReasoning;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly Dictionary<string, long> _toolStarts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deniedToolCalls = new(StringComparer.Ordinal);
    private string _shown = string.Empty;
    private string _shownReasoning = string.Empty;
    private int _answerStart;
    private string? _approvalToolCallId;
    private bool _contentMidLine;
    private bool _reasoningMidLine;
    private bool _thinkingAnnounced;
    private string? _phase;

    /// <param name="sameScreen">True when content and side reach one terminal, so a side line first ends the answer's open line.</param>
    public ConsoleTurnRenderer(TextWriter content, IAnsiConsole side, bool sameScreen, bool showReasoning, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _content = content;
        _side = side;
        _sameScreen = sameScreen;
        _showReasoning = showReasoning;
        _timeProvider = timeProvider;
    }

    public async Task RenderAsync(CliStreamEvent item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RenderCoreAsync(item, cancellationToken);
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
            // The runner decides an approval right after rendering it, so a denial belongs to the last approval's call.
            if (string.Equals(kind, TurnRunner.ApprovalDeniedKind, StringComparison.Ordinal) && _approvalToolCallId is { } denied)
            {
                _deniedToolCalls.Add(denied);
            }

            await SideLineAsync($"[yellow]{Markup.Escape(message)}[/]", cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FinishAsync(TurnOutcome outcome, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EndContentLineAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() =>
        _gate.Dispose();

    private static string Truncate(string text) =>
        text.Length <= MaxArgumentsChars ? text : string.Concat(text.AsSpan(0, MaxArgumentsChars), "...");

    // The node's default encoder writes quotes and non-ASCII as unicode escapes; re-encode so a person reads them literally.
    private static string Readable(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(ReadableJson) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private async Task RenderCoreAsync(CliStreamEvent item, CancellationToken cancellationToken)
    {
        var source = item.Source;
        switch (item.Kind)
        {
            case CliStreamEventKind.Status when source.RuntimePhase is { Length: > 0 } phase && !string.Equals(phase, _phase, StringComparison.Ordinal):
                _phase = phase;
                await SideLineAsync($"[grey]{Markup.Escape(phase)}...[/]", cancellationToken);
                break;
            case CliStreamEventKind.Delta:
                await ReasoningAsync(item.Reasoning, cancellationToken);
                await WriteContentAsync(item.Text, cancellationToken);
                break;
            case CliStreamEventKind.Snapshot:
                await CatchUpReasoningAsync(item.Reasoning, cancellationToken);
                await CatchUpAsync(item.Text, cancellationToken);
                break;
            case CliStreamEventKind.ToolActivity:
                await ToolLineAsync(item, cancellationToken);
                break;
            case CliStreamEventKind.ApprovalRequested:
                _approvalToolCallId = source.ToolCallId;
                var arguments = string.IsNullOrWhiteSpace(source.Arguments) ? string.Empty : $" [grey]{Markup.Escape(Truncate(Readable(source.Arguments)))}[/]";
                await SideLineAsync($"[yellow]Approval needed[/] for tool [bold]{Markup.Escape(source.ToolName ?? "unknown")}[/]{arguments}", cancellationToken);
                break;
            case CliStreamEventKind.QuestionRequested:
                await SideLineAsync("[yellow]The assistant has a question for you.[/]", cancellationToken);
                break;
            case CliStreamEventKind.Notice:
                await SideLineAsync($"[yellow]Notice:[/] {Markup.Escape(item.Text)}", cancellationToken);
                break;
            case CliStreamEventKind.Completed:
                await CatchUpAsync(item.Text, cancellationToken);
                break;
            case CliStreamEventKind.Cancelled:
                await CatchUpAsync(item.Text, cancellationToken);
                await SideLineAsync("[yellow]Cancelled.[/]", cancellationToken);
                break;
            case CliStreamEventKind.Failed:
                await SideLineAsync($"[red]Failed:[/] {Markup.Escape(source.Error ?? "the turn failed on the node.")}", cancellationToken);
                break;
            case CliStreamEventKind.Interrupted:
                await SideLineAsync($"[yellow]Interrupted:[/] {Markup.Escape(source.Error ?? "the turn stopped before it finished.")}", cancellationToken);
                break;
        }
    }

    private async Task ReasoningAsync(string reasoning, CancellationToken cancellationToken)
    {
        if (reasoning.Length == 0)
        {
            return;
        }

        _shownReasoning += reasoning;
        if (!_showReasoning)
        {
            if (!_thinkingAnnounced)
            {
                _thinkingAnnounced = true;
                await SideLineAsync("[grey]thinking...[/]", cancellationToken);
            }

            return;
        }

        if (_sameScreen)
        {
            await EndContentLineAsync(cancellationToken);
        }

        _side.Write(new Text(reasoning, new Style(decoration: Decoration.Dim)));
        _reasoningMidLine = !reasoning.EndsWith('\n');
    }

    private async Task ToolLineAsync(CliStreamEvent item, CancellationToken cancellationToken)
    {
        var source = item.Source;
        var name = Markup.Escape(source.ToolName ?? "tool");
        var key = source.ToolCallId ?? source.ToolName ?? string.Empty;
        if (!string.Equals(source.Type, ChatStreamEventTypes.ToolCallCompleted, StringComparison.Ordinal))
        {
            // The requested call's offset is where the node's terminal text (the answer after the last call) starts.
            if (source.ContentOffset is { } offset && offset > _answerStart)
            {
                _answerStart = (int)Math.Min(offset, int.MaxValue);
            }

            _toolStarts[key] = _timeProvider.GetTimestamp();
            await SideLineAsync($"[grey]> {name}[/]", cancellationToken);
            return;
        }

        var duration = _toolStarts.Remove(key, out var started)
            ? string.Create(CultureInfo.InvariantCulture, $" [grey]({_timeProvider.GetElapsedTime(started).TotalSeconds:0.0} s)[/]")
            : string.Empty;
        var flag = "[green]ok[/]";
        if (_deniedToolCalls.Contains(key))
        {
            flag = "[yellow]denied[/]";
        }
        else if (source.IsError == true)
        {
            flag = "[red]error[/]";
        }

        await SideLineAsync($"[grey]< {name}[/] {flag}{duration}", cancellationToken);
    }

    // A snapshot carries all text so far and a terminal the answer after the last tool call: print only what is unseen.
    private async Task CatchUpAsync(string full, CancellationToken cancellationToken)
    {
        if (full.StartsWith(_shown, StringComparison.Ordinal))
        {
            await WriteContentAsync(full[_shown.Length..], cancellationToken);
            return;
        }

        if (_answerStart > 0 && _answerStart <= _shown.Length && full.AsSpan().StartsWith(_shown.AsSpan(_answerStart), StringComparison.Ordinal))
        {
            await WriteContentAsync(full[(_shown.Length - _answerStart)..], cancellationToken);
            return;
        }

        await SideLineAsync("[grey](the node replaced the answer so far)[/]", cancellationToken);
        _shown = string.Empty;
        _answerStart = 0;
        await WriteContentAsync(full, cancellationToken);
    }

    private async Task CatchUpReasoningAsync(string full, CancellationToken cancellationToken)
    {
        if (full.StartsWith(_shownReasoning, StringComparison.Ordinal))
        {
            await ReasoningAsync(full[_shownReasoning.Length..], cancellationToken);
            return;
        }

        if (_showReasoning)
        {
            await SideLineAsync("[grey](the node replaced the reasoning so far)[/]", cancellationToken);
        }

        _shownReasoning = string.Empty;
        await ReasoningAsync(full, cancellationToken);
    }

    private async Task WriteContentAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (_reasoningMidLine)
        {
            _side.WriteLine();
            _reasoningMidLine = false;
        }

        await _content.WriteAsync(text.AsMemory(), cancellationToken);
        await _content.FlushAsync(cancellationToken);
        _shown += text;
        _contentMidLine = !text.EndsWith('\n');
    }

    private async Task SideLineAsync(string markup, CancellationToken cancellationToken)
    {
        if (_sameScreen)
        {
            await EndContentLineAsync(cancellationToken);
        }

        if (_reasoningMidLine)
        {
            _side.WriteLine();
            _reasoningMidLine = false;
        }

        _side.MarkupLine(markup);
    }

    private async Task EndContentLineAsync(CancellationToken cancellationToken)
    {
        if (_contentMidLine)
        {
            await _content.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken);
            await _content.FlushAsync(cancellationToken);
            _contentMidLine = false;
        }
    }
}
