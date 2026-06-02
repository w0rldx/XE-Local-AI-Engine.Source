namespace XE_Local_AI_Engine.HostAgent.Linux.Reconciliation;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

public sealed record ManifestReconcileResult
{
    public required bool Succeeded { get; init; }

    public required IReadOnlyList<RuntimeComponentStatusDto> Components { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }
}
