namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The pidfd calls and the run-token read a trainer signal goes through, swapped out by tests.</summary>
internal sealed class TrainerPidFdOps
{
    public static readonly TrainerPidFdOps Real = new()
    {
        Open = LinuxPidFd.Open,
        Send = static (pidFd, signal, group) => pidFd.Send(signal, group),
        ReadRunToken = LinuxTrainingProcessInspector.ReadRunToken
    };

    /// <summary>Every open reads unsupported, so each signal takes the numeric stat-then-kill path: the default behind every test seam.</summary>
    public static readonly TrainerPidFdOps Unsupported = new()
    {
        Open = static _ => LinuxPidFd.Unsupported(),
        Send = static (_, _, _) => throw new InvalidOperationException("An unsupported pidfd is never sent through."),
        ReadRunToken = static _ => throw new InvalidOperationException("Only a pidfd path re-reads the run token.")
    };

    public required Func<int, LinuxPidFd> Open { get; init; }

    public required Func<LinuxPidFd, int, bool, LinuxPidFdSendResult> Send { get; init; }

    /// <summary>The <c>XE_TRAINING_RUN_TOKEN</c> in <c>/proc/[pid]/environ</c>: null when unreadable or empty, an empty string when absent from a non-empty one.</summary>
    public required Func<int, string?> ReadRunToken { get; init; }
}
