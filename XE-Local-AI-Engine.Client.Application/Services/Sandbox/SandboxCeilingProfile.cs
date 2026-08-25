namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     WHICH set of CPU / memory / process-count ceilings a workload asks for. One axis rather than a boolean plus a
///     role-name switch inside <see cref="SandboxResourceCeilings" />: the declaration is the only thing that decides,
///     so the decision stays in <see cref="SandboxWorkloads" /> where a reviewer sees it, and the helper stays a pure
///     function of it.
/// </summary>
public enum SandboxCeilingProfile
{
    /// <summary>
    ///     No ceilings requested: the workload's commands are bounded only by their timeout and the machine. Nothing
    ///     declares this today; it exists so that making a role unbounded is a value someone has to write down.
    /// </summary>
    None = 0,

    /// <summary>
    ///     <c>run_python</c>'s ceilings, from the <c>Compute</c> section. Sized for a script that runs for a second or
    ///     two — deliberately tight, because arbitrary model-supplied code doing arithmetic needs very little and a
    ///     runaway loop should cost a bounded amount of the machine.
    /// </summary>
    ComputeTool = 1,

    /// <summary>
    ///     The host-toolchain roles' ceilings, from <c>LocalContainer:ToolchainLimits</c> — AgentHome, Coder, work
    ///     sessions and Development Mode, all of which run real compilers, test hosts and package restores.
    ///     <para>
    ///         Separate from <see cref="ComputeTool" /> by the operator's 2026-08-25 ruling, and the measurement that
    ///         produced it is on <see cref="SandboxToolchainLimits" />: sharing one set killed <c>dotnet build</c>
    ///         outright. A build is a fundamentally different workload from a calculation, and one number cannot be
    ///         right for both.
    ///     </para>
    /// </summary>
    HostToolchain = 2
}
