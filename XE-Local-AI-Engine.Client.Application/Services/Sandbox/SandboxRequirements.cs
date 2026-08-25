namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     What a workload needs from an execution substrate, on axes that have meaning to every backend. ADR 0007
///     Decision 1: a consumer declares requirements; it never names a backend.
///     <para>
///         <b>Engine-owned, from constants.</b> Every value in this record is composed by engine code in
///         <see cref="SandboxWorkloads" /> — never from configuration, never from a repository, never from anything a
///         model can write. That is not a style rule: a requirements record derivable from a repository would be
///         <c>devcontainer.json</c> under another name, which ADR 0004 §5 rejects wholesale.
///     </para>
///     <para>
///         <b>This is a declaration, not a request.</b> It is consulted once, when a backend is selected, and it is
///         deliberately NOT an extension of <see cref="SandboxCreateRequest" />. The two answer different questions at
///         different times: this one answers "which backend may serve this workload at all", at DI resolution, before
///         any sandbox exists; <see cref="SandboxCreateRequest" /> answers "what shape is THIS sandbox", per call,
///         against the backend already chosen. The per-call fields that carry the same axes — <see
///         cref="SandboxCreateRequest.Isolation" />, <see cref="SandboxCreateRequest.NetworkPolicy" />, <see
///         cref="SandboxCreateRequest.MaxJailDiskBytes" />, <see cref="SandboxCreateRequest.TrustedHostWorkspace" /> —
///         are unchanged and remain what the backend enforces. Nothing here weakens or replaces them.
///     </para>
/// </summary>
public sealed record SandboxRequirements
{
    /// <summary>
    ///     The workload this declaration belongs to, for the selector's resolution log and for the message a
    ///     fail-closed refusal carries. Diagnosis moved from "read the injected type" to "read the resolved backend
    ///     from the log line" when selection replaced naming, so the log line has to say whose resolution it is.
    /// </summary>
    public required string Workload { get; init; }

    /// <summary>Where the workload's compilers, SDKs and interpreters come from.</summary>
    public required SandboxToolchainSource Toolchain { get; init; }

    /// <summary>
    ///     The WEAKEST filesystem separation this workload will accept, as a PROPERTY: at
    ///     <see cref="SandboxIsolationMode.Filesystem" />, the host filesystem must be absent from the sandbox's view.
    ///     It does not name a mechanism, and is satisfied by any backend advertising
    ///     <see cref="SandboxProviderCapabilities.SupportsHostFilesystemBoundary" /> — the bubblewrap chain and a
    ///     hardened container both qualify. A workload that needs one mechanism's specific create-request contract
    ///     asks for it per call on <see cref="SandboxCreateRequest.Isolation" />, which is refused fail-closed by a
    ///     backend that does not implement it.
    ///     <para>
    ///         There is deliberately no default value: a declaration that omits it does not compile.
    ///     </para>
    ///     <para>
    ///         That is the second of the three mechanisms ADR 0007 Decision 4 uses to replace the compile-time guard.
    ///         A defaulted field would let a new consumer inherit the weakest posture by saying nothing, which is
    ///         exactly the failure mode the absent <c>implements</c> clause used to make impossible. Requiring the
    ///         value keeps "no unisolated fallback" a compiler-enforced property rather than a review one — note that
    ///         <see cref="SandboxCreateRequest.Isolation" /> does default, correctly, because it is a per-call request
    ///         against a backend already chosen for a workload that already declared its floor here.
    ///     </para>
    /// </summary>
    public required SandboxIsolationMode IsolationFloor { get; init; }

    /// <summary>
    ///     The WEAKEST egress posture this workload will accept — not the posture it asks for per call.
    ///     <para>
    ///         The distinction is load-bearing for AgentHome, which requests <see cref="SandboxNetworkPolicy.None" />
    ///         wherever the backend advertises it and <see cref="SandboxNetworkPolicy.Unrestricted" /> where it does
    ///         not (<see cref="SandboxEgressPolicy" />). Declaring <see cref="SandboxNetworkPolicy.None" />
    ///         as a floor there would not harden AgentHome; it would refuse to start it on Windows, where the
    ///         mechanism is not implemented at all. So AgentHome's floor is
    ///         <see cref="SandboxNetworkPolicy.Unrestricted" /> and its per-call tightening is unchanged.
    ///     </para>
    ///     <para>
    ///         A node that WANTS the refusal sets its section's <c>RequireEgressDenial</c> switch
    ///         (<see cref="SandboxOptions.RequireEgressDenial" />,
    ///         <see cref="DevelopmentSandboxOptions.RequireEgressDenial" />). That switch deliberately does not move
    ///         this floor: a floor of <see cref="SandboxNetworkPolicy.None" /> refuses at DI resolution with a
    ///         selection error, while the switch refuses at create time with a message naming the switch itself, which
    ///         is the difference between "this node cannot run the feature" and "this node was configured to require
    ///         denial".
    ///     </para>
    /// </summary>
    public required SandboxNetworkPolicy NetworkFloor { get; init; }

    /// <summary>
    ///     Whether this workload ASKS for CPU / memory / process-count ceilings on its create request.
    ///     <para>
    ///         Not a floor, and it constrains no candidate: <see cref="SandboxCreateRequest.ResourceLimits" /> is a
    ///         preference a backend may drop, so a workload that wants ceilings is not refused by a backend that cannot
    ///         impose them — it simply does not get them. That is why this is a plain declaration of intent rather than
    ///         a sixth axis in <c>SandboxProviderSelector.FindUnmetAxis</c>.
    ///     </para>
    ///     <para>
    ///         It exists because the operator-facing isolation summary reports the SERVED posture, and "the host can
    ///         impose ceilings" and "this role is given ceilings" are different facts. Every executing role asks today,
    ///         and every one of them derives the ceiling through <see cref="SandboxResourceCeilings" /> from one set of
    ///         node numbers — a role cannot pass ceilings its declaration does not claim, because the derivation takes
    ///         the declaration. <c>SandboxSubstrateSelectionArchitectureTests</c> enumerates both halves, and each
    ///         create site's own test asserts its request agrees with the constant, so the two cannot drift.
    ///     </para>
    ///     <para>
    ///         Required, for <see cref="IsolationFloor" />'s reason: a defaulted value would let a new consumer say
    ///         nothing and be reported as bounded — or unbounded — by accident.
    ///     </para>
    /// </summary>
    public required bool RequestsResourceLimits { get; init; }

    /// <summary>Whether the workload's writes have to outlive the sandbox.</summary>
    public required SandboxPersistence Persistence { get; init; }

    /// <summary>
    ///     Optional ceiling, in bytes, on what this workload's sandbox may leave on disk. Recorded on the declaration
    ///     because ADR 0007 names it as one of the five axes, and <see langword="null" /> everywhere today.
    ///     <para>
    ///         It constrains no candidate, and cannot: per <see cref="SandboxCreateRequest.MaxJailDiskBytes" /> the
    ///         ceiling may only TIGHTEN what the operator already allows, so a backend that ignores it is no worse off
    ///         than one that honours it and every backend satisfies it vacuously. It is stated here so the axis is
    ///         expressible — the effective per-call number stays on the create request, where it is read from
    ///         configuration and therefore could not live on an engine-owned constant.
    ///     </para>
    /// </summary>
    public long? MaxDiskBytes { get; init; }
}
