namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>What <see cref="ModelLabScriptRunner" /> or a serving worker spawns.</summary>
internal sealed class ModelLabScriptCommand
{
    public required string Interpreter { get; init; }

    public required string ScriptPath { get; init; }

    /// <summary>The handshake version this script must speak.</summary>
    public int ContractVersion { get; init; } = ModelLabTraceSpec.ContractVersion;

    /// <summary>False keeps phase and progress frames out of the event buffer entirely: they still count as liveness.</summary>
    public bool PublishPhases { get; init; } = true;
}
