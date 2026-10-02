namespace XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

/// <summary>One child process's recent output, as <see cref="IChildProcessOutputTails" /> reports it.</summary>
public sealed class ChildProcessOutputTail
{
    /// <summary>Process label, e.g. <c>llama-server[model/chat]</c>.</summary>
    public required string Label { get; init; }

    /// <summary>When the tail was registered (process spawn).</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>When the process exited, or <see langword="null" /> while it runs.</summary>
    public DateTimeOffset? ExitedUtc { get; init; }

    /// <summary>The retained, path-sanitized lines, oldest first.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}
