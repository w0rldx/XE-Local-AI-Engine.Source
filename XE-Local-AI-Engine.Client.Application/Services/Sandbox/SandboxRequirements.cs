namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     What a workload needs from an execution substrate, on axes meaningful to every backend (ADR 0007 Decision 1: a consumer declares
///     requirements, and never names a backend).
/// </summary>
/// <remarks>
///     Engine-owned: every value is composed from constants in <see cref="SandboxWorkloads" />, never from configuration, a repository or
///     anything a model can write — a requirements record derivable from a repository would be <c>devcontainer.json</c> under another name,
///     which ADR 0004 §5 rejects wholesale. It is a DECLARATION read once when a backend is selected, not an extension of
///     <see cref="SandboxCreateRequest" />: this answers "which backend may serve this workload at all" at DI resolution, that one answers
///     "what shape is THIS sandbox" per call against the chosen backend, and those per-call fields stay what the backend enforces.
/// </remarks>
public sealed record SandboxRequirements
{
    /// <summary>
    ///     The workload this declaration belongs to, named in the selector's resolution log and in the message a fail-closed refusal
    ///     carries.
    /// </summary>
    /// <remarks>
    ///     Selection replaced naming, so diagnosis moved from "read the injected type" to "read the resolved backend from the log line";
    ///     the log line therefore has to say whose resolution it is.
    /// </remarks>
    public required string Workload { get; init; }

    /// <summary>Where the workload's compilers, SDKs and interpreters come from.</summary>
    public required SandboxToolchainSource Toolchain { get; init; }

    /// <summary>
    ///     The WEAKEST filesystem separation this workload accepts, as a PROPERTY: at <see cref="SandboxIsolationMode.Filesystem" /> the
    ///     host filesystem must be absent from the sandbox's view.
    /// </summary>
    /// <remarks>
    ///     It names no mechanism and is satisfied by any backend advertising <c>SupportsHostFilesystemBoundary</c> — bubblewrap and a
    ///     hardened container both qualify; a workload needing one mechanism's create-request contract asks per call on
    ///     <see cref="SandboxCreateRequest.Isolation" />, refused fail-closed where unimplemented. There is deliberately NO default: a
    ///     declaration omitting it does not compile, so a new consumer cannot inherit the weakest posture by saying nothing (ADR 0007
    ///     Decision 4). That per-call request may default, being made against a backend already chosen.
    /// </remarks>
    public required SandboxIsolationMode IsolationFloor { get; init; }

    /// <summary>
    ///     The WEAKEST egress posture this workload accepts — not the posture it asks for per call.
    /// </summary>
    /// <remarks>
    ///     The distinction is load-bearing for AgentHome, which requests <see cref="SandboxNetworkPolicy.None" /> wherever the backend
    ///     advertises it and <see cref="SandboxNetworkPolicy.Unrestricted" /> where it does not (<see cref="SandboxEgressPolicy" />):
    ///     declaring None as a FLOOR would not harden AgentHome, it would refuse to start it on Windows, where the mechanism does not
    ///     exist. A node that WANTS that refusal sets its section's <c>RequireEgressDenial</c> switch, which deliberately leaves this floor
    ///     alone — a floor refuses at DI resolution with a selection error, the switch refuses at create time naming itself.
    /// </remarks>
    public required SandboxNetworkPolicy NetworkFloor { get; init; }

    /// <summary>
    ///     WHICH set of CPU, memory and process-count ceilings this workload asks for on its create request, or
    ///     <see cref="SandboxCeilingProfile.None" /> for none at all.
    /// </summary>
    /// <remarks>
    ///     Not a floor, and it constrains no candidate: <see cref="SandboxCreateRequest.ResourceLimits" /> is a preference a backend may
    ///     drop, so a workload that wants ceilings is not refused by a backend that cannot impose them. Every executing role derives its
    ///     numbers through <see cref="SandboxResourceCeilings" /> FROM this declaration, so a role cannot pass ceilings it did not claim.
    ///     An enum rather than a boolean because there are two sets: sharing the compute-tool numbers with the toolchain roles killed
    ///     <c>dotnet build</c> outright (<see cref="SandboxToolchainLimits" />). Required, for <see cref="IsolationFloor" />'s reason.
    /// </remarks>
    public required SandboxCeilingProfile Ceilings { get; init; }

    /// <summary>
    ///     Whether this workload asks for ceilings at all. Derived from <see cref="Ceilings" /> rather than stored, so
    ///     the operator-facing isolation summary and the axis a workload declares cannot disagree.
    /// </summary>
    public bool RequestsResourceLimits => Ceilings != SandboxCeilingProfile.None;

    /// <summary>Whether the workload's writes have to outlive the sandbox.</summary>
    public required SandboxPersistence Persistence { get; init; }

    /// <summary>
    ///     Optional ceiling, in bytes, on what this workload's sandbox may leave on disk; <see langword="null" /> everywhere today.
    /// </summary>
    /// <remarks>
    ///     Recorded because ADR 0007 names it as one of the five axes, but it constrains no candidate and cannot: per
    ///     <see cref="SandboxCreateRequest.MaxJailDiskBytes" /> the ceiling may only TIGHTEN what the operator already allows, so every
    ///     backend satisfies it vacuously. The effective per-call number stays on the create request, where it is read from configuration
    ///     and so could not live on an engine-owned constant.
    /// </remarks>
    public long? MaxDiskBytes { get; init; }
}
