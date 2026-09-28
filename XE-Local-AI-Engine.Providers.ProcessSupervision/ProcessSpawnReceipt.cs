namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     What a supervisor recorded about one runtime child it spawned: the identity the startup reaper must see again, field for
///     field, before it may signal that pid.
/// </summary>
public sealed class ProcessSpawnReceipt
{
    /// <summary>The child's pid.</summary>
    public required int Pid { get; init; }

    /// <summary>The child's <c>/proc/[pid]/stat</c> start time, read right after the spawn; a recycled pid carries a different one.</summary>
    public required long StartTicks { get; init; }

    /// <summary>The canonical path of the binary launched, as <c>/proc/[pid]/exe</c> reports it once the child has exec'd.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Diagnostics only: which model, role and port the child served.</summary>
    public required string Label { get; init; }
}
