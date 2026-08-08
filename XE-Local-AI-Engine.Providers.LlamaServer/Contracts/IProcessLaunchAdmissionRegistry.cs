namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The exact capacity-approved launch identity handed from admission to the detached supervisor spawn.</summary>
public sealed record ProcessLaunchAdmission(
    string ModelName,
    ModelRole Role,
    GpuVariant Variant,
    ResolvedLaunchArguments ResolvedArguments,
    ProcessContextAllocation Allocation);

/// <summary>A model/role identity tracked by the process-wide admission registry.</summary>
public readonly record struct ProcessLaunchAdmissionKey(string ModelName, ModelRole Role);

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
