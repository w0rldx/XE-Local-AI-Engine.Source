namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The ONE rule for what the node's sandbox security profile demands of a workload, shared by every sandbox create site and by the
///     operator-facing isolation summary (ADR 0020).
/// </summary>
/// <remarks>
///     <c>high</c> promotes every preference a workload DECLARES in <see cref="SandboxWorkloads" /> to a precondition and never adds an axis
///     it does not ask for: Development Mode and work sessions declare no filesystem boundary, so <c>high</c> never refuses them for one.
///     Floors are untouched under both profiles. The configuration's <c>RequireEgressDenial</c> keys stay tighten-only ABOVE the profile:
///     a key can add the egress precondition under <c>low</c>, and nothing here removes it. A caller never re-derives any of this, so a
///     summary row cannot report <c>Satisfied</c> while the create site it describes refuses.
/// </remarks>
public static class SandboxSecurityProfilePolicy
{
    /// <summary>
    ///     The requirement source <see cref="SandboxEgressPolicy.Resolve" /> names when the profile, not a configuration key, made egress
    ///     denial mandatory.
    /// </summary>
    public const string ProfileOptionKey = "sandbox security profile 'high'";

    /// <summary>The operator's remedy, appended to every refusal the profile causes.</summary>
    public const string Remedy = "choose 'low' on the Sandbox & isolation section of Node settings";

    /// <summary>The effective profile of a stored literal: <c>high</c> only when the literal is exactly <c>high</c>, everything else <c>low</c>.</summary>
    /// <remarks>Undecided (<see langword="null" />) and <c>pending</c> read as <c>low</c>, so a node that has not chosen behaves as before.</remarks>
    public static SandboxSecurityProfile Parse(string? stored)
    {
        return string.Equals(stored, StoredNodeSettings.SandboxSecurityProfileHigh, StringComparison.Ordinal)
            ? SandboxSecurityProfile.High
            : SandboxSecurityProfile.Low;
    }

    /// <summary>The wire and storage literal of an effective profile.</summary>
    public static string ToLiteral(SandboxSecurityProfile profile)
    {
        return profile == SandboxSecurityProfile.High
            ? StoredNodeSettings.SandboxSecurityProfileHigh
            : StoredNodeSettings.SandboxSecurityProfileLow;
    }

    /// <summary>Which axes are preconditions for <paramref name="requirements" /> under <paramref name="profile" />.</summary>
    /// <param name="requirements">The workload's declaration from <see cref="SandboxWorkloads" />.</param>
    /// <param name="profile">The node's effective profile, read per call.</param>
    /// <param name="configRequiresEgressDenial">The <c>RequireEgressDenial</c> key of the options section that constrains this workload.</param>
    /// <remarks>
    ///     Egress is promoted only for a workload whose network floor is <see cref="SandboxNetworkPolicy.Unrestricted" />: one with a
    ///     <see cref="SandboxNetworkPolicy.None" /> floor already denies egress by its own declaration. The filesystem boundary is promoted
    ///     only where the declaration prefers one (<see cref="SandboxRequirements.RequestsFilesystemIsolationWhereAdvertised" />); a floor
    ///     already refuses without it.
    /// </remarks>
    public static SandboxProfilePreconditions Preconditions(SandboxRequirements requirements,
        SandboxSecurityProfile profile,
        bool configRequiresEgressDenial)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var high = profile == SandboxSecurityProfile.High;
        return new SandboxProfilePreconditions
        {
            RequireEgressDenial = configRequiresEgressDenial || (high && requirements.NetworkFloor == SandboxNetworkPolicy.Unrestricted),
            RequireFilesystemBoundary = high && requirements.RequestsFilesystemIsolationWhereAdvertised,
            RequireResourceCeilings = high && requirements.RequestsResourceLimits
        };
    }

    /// <summary>The required axes a backend advertising <paramref name="capabilities" /> cannot serve, as operator-facing phrases; empty when it serves them all.</summary>
    /// <remarks>
    ///     "Served" is what the create site would get: egress through <see cref="SandboxEgressPolicy" /> (a <c>None</c>-floor workload's
    ///     egress is its isolated chain's and is not counted here), the boundary through <see cref="SandboxRequirements.RequestedIsolation" />,
    ///     so the Preview AppContainer boundary never satisfies the preference (ADR 0019 Decision 10), and ceilings through the capability
    ///     <see cref="SandboxResourceCeilings.Resolve" /> gates on.
    /// </remarks>
    public static IReadOnlyList<string> UnservedAxes(SandboxRequirements requirements,
        SandboxProviderCapabilities capabilities,
        SandboxProfilePreconditions preconditions)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(preconditions);

        var unserved = new List<string>(3);
        if (preconditions.RequireEgressDenial
            && requirements.NetworkFloor == SandboxNetworkPolicy.Unrestricted
            && !capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy))
        {
            unserved.Add("egress denied");
        }

        if (preconditions.RequireFilesystemBoundary && requirements.RequestedIsolation(capabilities) != SandboxIsolationMode.Filesystem)
        {
            unserved.Add("a Stable host-filesystem boundary");
        }

        if (preconditions.RequireResourceCeilings && !capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits))
        {
            unserved.Add("CPU, memory and process-count ceilings");
        }

        return unserved;
    }

    /// <summary>Whether a create for <paramref name="requirements" /> on this backend is refused under <paramref name="profile" />.</summary>
    /// <remarks>What the capability summary lists as the roles <c>high</c> would refuse on this host.</remarks>
    public static bool Refuses(SandboxRequirements requirements,
        SandboxProviderCapabilities capabilities,
        SandboxSecurityProfile profile,
        bool configRequiresEgressDenial)
    {
        return UnservedAxes(requirements, capabilities, Preconditions(requirements, profile, configRequiresEgressDenial)).Count > 0;
    }

    /// <summary>The egress posture for an agent-facing create request, refusing where denial is required and cannot be served.</summary>
    /// <param name="requirements">The workload's declaration; its <see cref="SandboxRequirements.Workload" /> names the refused role.</param>
    /// <param name="capabilities">The resolved backend's advertised capabilities.</param>
    /// <param name="profile">The node's effective profile, read per call.</param>
    /// <param name="configRequiresEgressDenial">The section's <c>RequireEgressDenial</c> key.</param>
    /// <param name="configOptionKey">That key's name, named by the refusal when the key rather than the profile made denial mandatory.</param>
    /// <exception cref="SandboxCapabilityNotSupportedException">Denial is required and this backend cannot deny egress.</exception>
    public static SandboxNetworkPolicy ResolveEgress(SandboxRequirements requirements,
        SandboxProviderCapabilities capabilities,
        SandboxSecurityProfile profile,
        bool configRequiresEgressDenial,
        string configOptionKey)
    {
        var preconditions = Preconditions(requirements, profile, configRequiresEgressDenial);
        return SandboxEgressPolicy.Resolve(capabilities,
            preconditions.RequireEgressDenial,
            configRequiresEgressDenial ? configOptionKey : ProfileOptionKey,
            requirements.Workload);
    }

    /// <summary>
    ///     Refuses the create when the profile requires a filesystem boundary or ceilings this backend cannot serve. Egress is not checked
    ///     here: <see cref="ResolveEgress" /> decides it while building the request.
    /// </summary>
    /// <exception cref="SandboxCapabilityNotSupportedException">A required boundary or ceiling cannot be served; the message names the profile and the remedy.</exception>
    public static void EnsureServed(SandboxRequirements requirements,
        SandboxProviderCapabilities capabilities,
        SandboxSecurityProfile profile)
    {
        // Egress is ResolveEgress's, so it can name the configuration key when the key made it mandatory; this checks the other two.
        var preconditions = Preconditions(requirements, profile, configRequiresEgressDenial: false);
        var unserved = UnservedAxes(requirements,
            capabilities,
            new SandboxProfilePreconditions
            {
                RequireEgressDenial = false,
                RequireFilesystemBoundary = preconditions.RequireFilesystemBoundary,
                RequireResourceCeilings = preconditions.RequireResourceCeilings
            });
        if (unserved.Count == 0)
        {
            return;
        }

        throw new SandboxCapabilityNotSupportedException(
            $"The {ProfileOptionKey} is in force, so the '{requirements.Workload}' sandbox may only run with {string.Join(" and ", unserved)} — but the resolved sandbox "
            + "backend cannot provide that on this host. Install the missing mechanism the sandbox isolation summary names, or " + Remedy
            + " to accept that this role runs without it.");
    }
}
