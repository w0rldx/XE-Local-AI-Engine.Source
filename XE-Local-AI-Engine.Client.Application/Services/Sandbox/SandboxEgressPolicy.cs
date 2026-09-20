namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The ONE place that answers "what egress posture does an agent-facing sandbox ask for on this node", for all three agent-facing
///     create sites.
/// </summary>
/// <remarks>
///     By default the request is <see cref="SandboxNetworkPolicy.None" /> wherever the backend advertises network confinement and
///     <see cref="SandboxNetworkPolicy.Unrestricted" /> where it does not, hardening the nodes that can be hardened without refusing to run
///     on Windows or where <c>unshare</c> failed; the node's <c>RequireEgressDenial</c> switch makes denial a precondition instead. It
///     throws here rather than letting the backend refuse, because the backend's rejection names the missing mechanism and cannot name the
///     setting that made it mandatory; the exception type is the same, so every catch site is unchanged.
/// </remarks>
public static class SandboxEgressPolicy
{
    /// <summary>The per-node switch's key on the AgentHome section, for the refusal message and the isolation summary.</summary>
    public const string AgentOptionKey = SandboxOptions.SectionName + ":" + nameof(SandboxOptions.RequireEgressDenial);

    /// <summary>The per-node switch's key on the Development section.</summary>
    public const string DevelopmentOptionKey =
        DevelopmentSandboxOptions.SectionName + ":" + nameof(DevelopmentSandboxOptions.RequireEgressDenial);

    /// <summary>The egress posture to put on a create request.</summary>
    /// <param name="capabilities">The resolved backend's advertised capabilities.</param>
    /// <param name="required">
    ///     The node's <c>RequireEgressDenial</c> switch for this role's section; when set, a backend that cannot confine the network is
    ///     refused rather than served unrestricted.
    /// </param>
    /// <param name="optionKey">
    ///     The key <paramref name="required" /> was read from, so the refusal names the switch an operator would clear:
    ///     <see cref="AgentOptionKey" /> or <see cref="DevelopmentOptionKey" />.
    /// </param>
    /// <param name="workload">The declaring workload's name, so the refusal says which role could not start.</param>
    /// <exception cref="SandboxCapabilityNotSupportedException">
    ///     <paramref name="required" /> is set and this backend cannot deny egress.
    /// </exception>
    public static SandboxNetworkPolicy Resolve(SandboxProviderCapabilities capabilities,
        bool required,
        string optionKey,
        string workload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);

        if (capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy))
        {
            return SandboxNetworkPolicy.None;
        }

        return required
            ? throw new SandboxCapabilityNotSupportedException($"'{optionKey}' is set, so the '{workload}' sandbox may only run with egress denied — but the resolved sandbox "
                                                               + "backend does not advertise network confinement on this host, so it cannot deny it. Install the missing "
                                                               + $"mechanism (on Linux, the user-namespace support the sandbox containment probe reports as unavailable), or clear '{optionKey}' "
                                                               + "to accept that this role runs with the host's network.")
            : SandboxNetworkPolicy.Unrestricted;
    }

    /// <summary>
    ///     Whether egress denial is a PRECONDITION for a role rather than a best-effort tightening — what the operator-facing isolation
    ///     summary reports as "required" versus "where available".
    /// </summary>
    /// <remarks>
    ///     True for a workload whose own declaration will not accept egress (<c>run_python</c>, whose
    ///     <see cref="SandboxRequirements.NetworkFloor" /> is <see cref="SandboxNetworkPolicy.None" />, its backend refused at selection
    ///     without it), and for any role on a node that set its <c>RequireEgressDenial</c> switch. The switch deliberately does NOT move
    ///     the floor: moving it would refuse the workload at startup with a selection error, while this refuses at create time with a
    ///     message naming the switch.
    /// </remarks>
    public static bool IsRequired(SandboxRequirements requirements, bool nodeRequiresDenial)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        return nodeRequiresDenial || requirements.NetworkFloor != SandboxNetworkPolicy.Unrestricted;
    }
}
