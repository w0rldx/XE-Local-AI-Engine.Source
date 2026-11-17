namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>The first worker receipt a reap left in place, as a refusal names it.</summary>
internal sealed class ModelLabUnconfirmedWorker
{
    /// <summary>"pid N" for a receipt that names one; otherwise the receipt file, which the operator must inspect and remove.</summary>
    public required string Name { get; init; }

    /// <summary>Reaped from this host's in-memory copy, which is too incomplete to probe: no file removal clears it, only a node restart.</summary>
    public required bool HeldUnprobeable { get; init; }
}
