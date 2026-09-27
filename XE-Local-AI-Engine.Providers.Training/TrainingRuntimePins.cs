namespace XE_Local_AI_Engine.Providers.Training;

/// <summary>The handshake contract the provisioned training runtime must report.</summary>
/// <remarks>The uv release it is provisioned with is pinned in <c>ManagedPythonPins</c>, shared with the compute tool.</remarks>
public static class TrainingRuntimePins
{
    /// <summary>The handshake version <c>tools/training/probe.py</c> emits.</summary>
    /// <remarks>
    ///     A provisioned runtime whose probe reports a different value is rejected rather than adopted: the scripts and
    ///     the managed side are versioned together, so a mismatch means the two halves are out of step and nothing
    ///     downstream can be trusted.
    /// </remarks>
    public const int ProbeContractVersion = 1;
}
