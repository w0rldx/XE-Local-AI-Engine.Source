namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     One coalesced, already-redacted slice of the output of the sandbox command a tool call runs, plus that command's
///     current label, running flag and exit code, so every payload describes the command on its own.
/// </summary>
/// <remarks>
///     Observe-only and lossy by design: it never enters the tool lifecycle history, and only the resume registry keeps
///     a bounded tail of it. <see cref="Replace" /> starts the call's output over (a new command, or a flood's final tail).
/// </remarks>
public sealed class ToolCallOutputPayload
{
    public required Guid InvocationId { get; init; }

    public required string ToolCallId { get; init; }

    /// <summary>The redacted lines since the previous payload, oldest first; may be empty.</summary>
    public required IReadOnlyList<NodeChatToolOutputLine> Lines { get; init; }

    /// <summary>Lines of the current command not delivered live so far (cumulative for that command).</summary>
    public required int SkippedLines { get; init; }

    /// <summary>The redacted executable and arguments of the current command; null when the tool names none.</summary>
    public string? CommandLabel { get; init; }

    public required bool CommandRunning { get; init; }

    /// <summary>The exit code once the command finished on its own; null while it runs, or when it was stopped or failed to run.</summary>
    public int? CommandExitCode { get; init; }

    /// <summary>The lines replace the call's output so far instead of extending it.</summary>
    public bool Replace { get; init; }
}
