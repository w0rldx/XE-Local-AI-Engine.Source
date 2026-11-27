namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>How one Model Lab script or serving-worker job ended: its stream state, the watchdog's reason and the exit code.</summary>
internal sealed class ModelLabScriptRun
{
    public required ModelLabScriptStreamState State { get; init; }

    public required string? FailureReason { get; init; }

    public required int ExitCode { get; init; }

    /// <summary>What the job ends with because of the script, or <see langword="null" /> when the script did its work.</summary>
    public ModelLabExperimentCompletion? FailureOrNull(bool cancelRequested)
    {
        if (cancelRequested || State.ScriptCancelled || ExitCode == TrainingRunExecutor.CancelledExitCode)
        {
            return ModelLabExperimentCompletion.Cancelled();
        }

        if (FailureReason is { } reason)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, reason);
        }

        if (State.ErrorMessage is { Length: > 0 } error)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, State.WithLastOutput(error));
        }

        if (!State.HandshakeSeen || !State.Done || ExitCode != 0)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, State.WithLastOutput($"The Model Lab script exited with status {ExitCode}."));
        }

        return null;
    }
}
