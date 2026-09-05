namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The exact capacity-approved launch identity handed from admission to the detached supervisor spawn.</summary>
/// <param name="GlobalFreeVramBytesAtAdmission">
///     Report-only: machine-global free VRAM as the capacity gate read it under its decision gate while approving this
///     launch, so the supervisor can put it on the load observation without a second probe. Null when the gate had no
///     readable figure (a non-NVIDIA or CPU-only host), and null on every admission built outside that gate. NOTHING
///     may branch on it — the registry ignores it, and admission arithmetic stays in the capacity service.
/// </param>
public sealed record ProcessLaunchAdmission(
    string ModelName,
    ModelRole Role,
    GpuVariant Variant,
    ResolvedLaunchArguments ResolvedArguments,
    ProcessContextAllocation Allocation,
    long? GlobalFreeVramBytesAtAdmission = null);

/// <summary>A model/role identity tracked by the process-wide admission registry.</summary>
public readonly record struct ProcessLaunchAdmissionKey(string ModelName, ModelRole Role)
{
    public bool Equals(ProcessLaunchAdmissionKey other) =>
        Role == other.Role && string.Equals(ModelName, other.ModelName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(ModelName), Role);
}

/// <summary>A lock-consistent registry snapshot used by the capacity decision gate.</summary>
public sealed record ProcessLaunchAdmissionSnapshot(
    IReadOnlySet<ProcessLaunchAdmissionKey> AdmittedKeys,
    bool HasRequestedKey,
    bool HasGlobalBlocker);

/// <summary>Idempotent capacity-owner lease for a published launch admission.</summary>
public interface IProcessLaunchAdmissionLease : IDisposable;

/// <summary>Idempotent detached-launch ticket retaining a captured admission until the spawn settles.</summary>
public interface IProcessLaunchTicket : IDisposable;

/// <summary>Process-wide exact admission-to-launch handoff registry.</summary>
public interface IProcessLaunchAdmissionRegistry
{
    ProcessLaunchAdmissionSnapshot Snapshot(string modelName, ModelRole role);

    IProcessLaunchAdmissionLease? Acquire(ProcessLaunchAdmission admission);

    bool TryAcquire(ProcessLaunchAdmission admission, out IProcessLaunchAdmissionLease? lease);

    bool TryBeginLaunch(string modelName,
        ModelRole role,
        out ProcessLaunchAdmission? admission,
        out IProcessLaunchTicket? ticket);
}
