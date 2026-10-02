namespace XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

/// <summary>Read side of the recent child-process output tails (llama-server, sd-server, whisper-server).</summary>
public interface IChildProcessOutputTails
{
    /// <summary>The retained tails, newest first; each tail's lines are already path-sanitized.</summary>
    IReadOnlyList<ChildProcessOutputTail> Snapshot();
}
