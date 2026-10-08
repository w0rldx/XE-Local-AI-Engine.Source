namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Reflection;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The ONE rule the sandbox security profile adds (ADR 0020), as a matrix: profile × every <see cref="SandboxWorkloads" /> declaration ×
///     the capability sets a real host presents.
/// </summary>
/// <remarks>
///     Expected values are written out per row rather than re-derived from the declaration, so a change to the rule or a declaration shows
///     up as a named row that moved.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class SandboxSecurityProfilePolicyTests
{
    internal const SandboxProviderCapabilities None = SandboxProviderCapabilities.None;
    internal const SandboxProviderCapabilities EgressOnly = SandboxProviderCapabilities.SupportsNetworkPolicy;

    internal const SandboxProviderCapabilities BoundaryAndEgress =
        SandboxProviderCapabilities.SupportsFilesystemIsolation | SandboxProviderCapabilities.SupportsNetworkPolicy;

    internal const SandboxProviderCapabilities AllThree = BoundaryAndEgress | SandboxProviderCapabilities.SupportsResourceLimits;

    // The Windows shape with execution previews on: the boundary flag through the Preview AppContainer mechanism, and nothing else.
    internal const SandboxProviderCapabilities AppContainerOnly =
        SandboxProviderCapabilities.SupportsFilesystemIsolation | SandboxProviderCapabilities.SupportsAppContainerBoundary;

    /// <summary>The preconditions under <c>high</c> per declaration: (egress, boundary, ceilings), the configuration key unset.</summary>
    [Test]
    [Arguments(nameof(SandboxWorkloads.AgentHome), true, true, true)]
    [Arguments(nameof(SandboxWorkloads.Coder), true, true, true)]
    [Arguments(nameof(SandboxWorkloads.WorkSession), true, false, true)]
    [Arguments(nameof(SandboxWorkloads.RunPython), false, false, true)]
    [Arguments(nameof(SandboxWorkloads.McpStdio), false, false, true)]
    [Arguments(nameof(SandboxWorkloads.DevelopmentModeHostToolchain), true, false, true)]
    [Arguments(nameof(SandboxWorkloads.DevelopmentModeImageToolchain), true, false, true)]
    public void Preconditions_UnderHigh_AreTheDeclaredPreferences(string declaration, bool egress, bool boundary, bool ceilings)
    {
        var preconditions = SandboxSecurityProfilePolicy.Preconditions(Declaration(declaration), SandboxSecurityProfile.High, configRequiresEgressDenial: false);

        AssertEx.Equal(egress, preconditions.RequireEgressDenial);
        AssertEx.Equal(boundary, preconditions.RequireFilesystemBoundary);
        AssertEx.Equal(ceilings, preconditions.RequireResourceCeilings);
    }

    /// <summary><c>low</c> yields exactly <c>config || false</c> for egress and false for the other two, on every declaration.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void Preconditions_UnderLow_AreTheConfigurationKeyAndNothingElse(bool configKey)
    {
        foreach (var requirements in Declarations())
        {
            var preconditions = SandboxSecurityProfilePolicy.Preconditions(requirements, SandboxSecurityProfile.Low, configKey);

            AssertEx.Equal(configKey, preconditions.RequireEgressDenial, requirements.Workload);
            AssertEx.False(preconditions.RequireFilesystemBoundary, requirements.Workload);
            AssertEx.False(preconditions.RequireResourceCeilings, requirements.Workload);
        }
    }

    /// <summary>The configuration key stays tighten-only ABOVE the profile: <c>high</c> can never remove the egress requirement it adds.</summary>
    [Test]
    public void Preconditions_TheConfigurationKeyStillRequiresEgressUnderHigh_EvenForANoneFloorWorkload()
    {
        var preconditions = SandboxSecurityProfilePolicy.Preconditions(SandboxWorkloads.RunPython, SandboxSecurityProfile.High, configRequiresEgressDenial: true);

        AssertEx.True(preconditions.RequireEgressDenial);
    }

    /// <summary>Whether a create is refused under <c>high</c>, per declaration × capability set: only a host serving all three axes serves everything.</summary>
    /// <remarks>
    ///     The AppContainer-only host serves no PREFERRED boundary (ADR 0019 Decision 10), egress or ceilings; a None-floor workload's egress
    ///     is its isolated chain's, so it is never counted against <c>run_python</c> or Sandboxed MCP.
    /// </remarks>
    [Test]
    // AgentHome: egress, boundary and ceilings all required.
    [Arguments(nameof(SandboxWorkloads.AgentHome), None, 3)]
    [Arguments(nameof(SandboxWorkloads.AgentHome), EgressOnly, 2)]
    [Arguments(nameof(SandboxWorkloads.AgentHome), BoundaryAndEgress, 1)]
    [Arguments(nameof(SandboxWorkloads.AgentHome), AllThree, 0)]
    [Arguments(nameof(SandboxWorkloads.AgentHome), AppContainerOnly, 3)]
    // Work sessions and Development Mode: egress and ceilings, never a boundary they do not ask for.
    [Arguments(nameof(SandboxWorkloads.WorkSession), None, 2)]
    [Arguments(nameof(SandboxWorkloads.WorkSession), EgressOnly, 1)]
    [Arguments(nameof(SandboxWorkloads.WorkSession), BoundaryAndEgress, 1)]
    [Arguments(nameof(SandboxWorkloads.WorkSession), AllThree, 0)]
    [Arguments(nameof(SandboxWorkloads.WorkSession), AppContainerOnly, 2)]
    [Arguments(nameof(SandboxWorkloads.DevelopmentModeHostToolchain), None, 2)]
    [Arguments(nameof(SandboxWorkloads.DevelopmentModeHostToolchain), EgressOnly, 1)]
    [Arguments(nameof(SandboxWorkloads.DevelopmentModeHostToolchain), AllThree, 0)]
    // Floor workloads: only the ceilings are the profile's.
    [Arguments(nameof(SandboxWorkloads.RunPython), None, 1)]
    [Arguments(nameof(SandboxWorkloads.RunPython), BoundaryAndEgress, 1)]
    [Arguments(nameof(SandboxWorkloads.RunPython), AllThree, 0)]
    [Arguments(nameof(SandboxWorkloads.RunPython), AppContainerOnly, 1)]
    [Arguments(nameof(SandboxWorkloads.McpStdio), BoundaryAndEgress, 1)]
    [Arguments(nameof(SandboxWorkloads.McpStdio), AllThree, 0)]
    [Arguments(nameof(SandboxWorkloads.McpStdio), AppContainerOnly, 1)]
    public void UnservedAxes_UnderHigh_CountWhatTheHostCannotServe(string declaration, SandboxProviderCapabilities capabilities, int unserved)
    {
        var requirements = Declaration(declaration);
        var preconditions = SandboxSecurityProfilePolicy.Preconditions(requirements, SandboxSecurityProfile.High, configRequiresEgressDenial: false);

        AssertEx.Equal(unserved, SandboxSecurityProfilePolicy.UnservedAxes(requirements, capabilities, preconditions).Count);
        AssertEx.Equal(unserved > 0, SandboxSecurityProfilePolicy.Refuses(requirements, capabilities, SandboxSecurityProfile.High, configRequiresEgressDenial: false));
    }

    /// <summary>
    ///     <c>low</c> without the configuration key refuses nothing anywhere: the byte-identical half. With the key it refuses exactly
    ///     where egress cannot be denied, as the key did before the profile existed.
    /// </summary>
    [Test]
    [Arguments(None)]
    [Arguments(EgressOnly)]
    [Arguments(BoundaryAndEgress)]
    [Arguments(AllThree)]
    [Arguments(AppContainerOnly)]
    public void Refuses_UnderLow_OnlyWhereTheConfigurationKeyAsksForEgressTheHostCannotDeny(SandboxProviderCapabilities capabilities)
    {
        foreach (var requirements in Declarations())
        {
            AssertEx.False(SandboxSecurityProfilePolicy.Refuses(requirements, capabilities, SandboxSecurityProfile.Low, configRequiresEgressDenial: false),
                requirements.Workload);

            var expectedWithKey = requirements.NetworkFloor == SandboxNetworkPolicy.Unrestricted
                                  && !capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy);
            AssertEx.Equal(expectedWithKey,
                SandboxSecurityProfilePolicy.Refuses(requirements, capabilities, SandboxSecurityProfile.Low, configRequiresEgressDenial: true),
                requirements.Workload);
        }
    }

    [Test]
    public void EnsureServed_UnderHigh_OnAHostMissingTheBoundaryAndCeilings_NamesTheProfileTheAxesAndTheRemedy()
    {
        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() =>
            SandboxSecurityProfilePolicy.EnsureServed(SandboxWorkloads.AgentHome, EgressOnly, SandboxSecurityProfile.High));

        AssertEx.Contains(exception.Message, SandboxSecurityProfilePolicy.ProfileOptionKey);
        AssertEx.Contains(exception.Message, SandboxWorkloads.AgentHome.Workload);
        AssertEx.Contains(exception.Message, "boundary");
        AssertEx.Contains(exception.Message, "ceilings");
        AssertEx.Contains(exception.Message, SandboxSecurityProfilePolicy.Remedy);
    }

    // Egress is ResolveEgress's to refuse, and a configuration key is irrelevant here: EnsureServed never throws for a missing egress
    // mechanism, and never under low.
    [Test]
    [Arguments(SandboxSecurityProfile.High, BoundaryAndEgress | SandboxProviderCapabilities.SupportsResourceLimits)]
    [Arguments(SandboxSecurityProfile.High, SandboxProviderCapabilities.SupportsFilesystemIsolation | SandboxProviderCapabilities.SupportsResourceLimits)]
    [Arguments(SandboxSecurityProfile.Low, None)]
    public void EnsureServed_DoesNotThrow_WhenTheBoundaryAndCeilingsAreServedOrTheProfileIsLow(SandboxSecurityProfile profile,
        SandboxProviderCapabilities capabilities)
    {
        SandboxSecurityProfilePolicy.EnsureServed(SandboxWorkloads.AgentHome, capabilities, profile);
    }

    [Test]
    public void ResolveEgress_UnderHighWithoutTheKey_NamesTheProfileNotTheKey()
    {
        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() =>
            SandboxSecurityProfilePolicy.ResolveEgress(SandboxWorkloads.AgentHome, None, SandboxSecurityProfile.High, false, SandboxEgressPolicy.AgentOptionKey));

        AssertEx.Contains(exception.Message, SandboxSecurityProfilePolicy.ProfileOptionKey);
        AssertEx.Contains(exception.Message, SandboxSecurityProfilePolicy.Remedy);
        AssertEx.False(exception.Message.Contains(SandboxEgressPolicy.AgentOptionKey, StringComparison.Ordinal));
    }

    // The key, when set, is what the operator set, so it is what the refusal names — and the message is the one it was before the profile.
    [Test]
    [Arguments(SandboxSecurityProfile.Low)]
    [Arguments(SandboxSecurityProfile.High)]
    public void ResolveEgress_WithTheKeySet_NamesTheKey(SandboxSecurityProfile profile)
    {
        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() =>
            SandboxSecurityProfilePolicy.ResolveEgress(SandboxWorkloads.AgentHome, None, profile, true, SandboxEgressPolicy.AgentOptionKey));

        AssertEx.Contains(exception.Message, $"'{SandboxEgressPolicy.AgentOptionKey}' is set");
        AssertEx.Contains(exception.Message, $"clear '{SandboxEgressPolicy.AgentOptionKey}'");
    }

    [Test]
    [Arguments(SandboxSecurityProfile.Low, AllThree, SandboxNetworkPolicy.None)]
    [Arguments(SandboxSecurityProfile.High, AllThree, SandboxNetworkPolicy.None)]
    [Arguments(SandboxSecurityProfile.Low, None, SandboxNetworkPolicy.Unrestricted)]
    public void ResolveEgress_ServesTheSamePostureAsBeforeWhereItDoesNotRefuse(SandboxSecurityProfile profile,
        SandboxProviderCapabilities capabilities,
        SandboxNetworkPolicy expected)
    {
        AssertEx.Equal(expected,
            SandboxSecurityProfilePolicy.ResolveEgress(SandboxWorkloads.AgentHome, capabilities, profile, false, SandboxEgressPolicy.AgentOptionKey));
    }

    /// <summary>Only the exact literal <c>high</c> is <c>high</c>; undecided, <c>pending</c>, <c>low</c> and junk all run under <c>low</c>.</summary>
    [Test]
    [Arguments(StoredNodeSettings.SandboxSecurityProfileHigh, SandboxSecurityProfile.High)]
    [Arguments(StoredNodeSettings.SandboxSecurityProfileLow, SandboxSecurityProfile.Low)]
    [Arguments(StoredNodeSettings.SandboxSecurityProfilePending, SandboxSecurityProfile.Low)]
    [Arguments(null, SandboxSecurityProfile.Low)]
    [Arguments("High", SandboxSecurityProfile.Low)]
    public void Parse_OnlyTheExactHighLiteralIsHigh(string? stored, SandboxSecurityProfile expected)
    {
        AssertEx.Equal(expected, SandboxSecurityProfilePolicy.Parse(stored));
    }

    [Test]
    [Arguments(SandboxSecurityProfile.Low, StoredNodeSettings.SandboxSecurityProfileLow)]
    [Arguments(SandboxSecurityProfile.High, StoredNodeSettings.SandboxSecurityProfileHigh)]
    public void ToLiteral_RoundTripsThroughParse(SandboxSecurityProfile profile, string literal)
    {
        AssertEx.Equal(literal, SandboxSecurityProfilePolicy.ToLiteral(profile));
        AssertEx.Equal(profile, SandboxSecurityProfilePolicy.Parse(literal));
    }

    private static SandboxRequirements Declaration(string name)
    {
        return (SandboxRequirements)typeof(SandboxWorkloads).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    }

    private static IEnumerable<SandboxRequirements> Declarations()
    {
        return typeof(SandboxWorkloads)
               .GetFields(BindingFlags.Public | BindingFlags.Static)
               .Where(static field => field.FieldType == typeof(SandboxRequirements))
               .Select(static field => (SandboxRequirements)field.GetValue(null)!);
    }
}
