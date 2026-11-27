namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>How one job in a serving worker ended, and whether the worker may take another.</summary>
internal sealed class ModelLabServeJobOutcome
{
    public required ModelLabScriptRun Run { get; init; }

    /// <summary>True only after a <c>done</c> and the worker's <c>ready</c>, with the frame order intact.</summary>
    public required bool Reusable { get; init; }
}
