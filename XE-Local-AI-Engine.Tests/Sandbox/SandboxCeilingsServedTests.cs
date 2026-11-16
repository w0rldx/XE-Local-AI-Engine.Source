namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Reflection;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="SandboxRequirements.CeilingsServed" />, the one rule every ceilings consumer reads (ADR 0025), as a matrix of every
///     declaration over the capability sets a real host presents. Expected rows are written out, so a moved row is a named change.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SandboxCeilingsServedTests
{
    internal const SandboxProviderCapabilities Limits = SandboxProviderCapabilities.SupportsResourceLimits;
    internal const SandboxProviderCapabilities Bwrap = SandboxProviderCapabilities.SupportsFilesystemIsolation | SandboxProviderCapabilities.SupportsHostFilesystemBoundary;
    internal const SandboxProviderCapabilities AppContainer = Bwrap | SandboxProviderCapabilities.SupportsAppContainerBoundary;

    private static readonly string[] Plain =
    [
        nameof(SandboxWorkloads.AgentHome), nameof(SandboxWorkloads.Coder), nameof(SandboxWorkloads.WorkSession),
        nameof(SandboxWorkloads.DevelopmentModeHostToolchain), nameof(SandboxWorkloads.DevelopmentModeImageToolchain)
    ];

    private static readonly string[] Every = [.. Plain, nameof(SandboxWorkloads.RunPython), nameof(SandboxWorkloads.McpStdio)];

    [Test]
    // Linux with bubblewrap under a systemd scope: every role, floor roles through the chain's own scope.
    [Arguments("linux-full", Limits | Bwrap | SandboxProviderCapabilities.SupportsNetworkPolicy, true, true)]
    // Linux with a scope but no bwrap: the floor roles run nowhere, so nothing carries their ceilings.
    [Arguments("linux-no-bwrap", Limits | SandboxProviderCapabilities.SupportsNetworkPolicy, true, false)]
    // Windows with Job Object ceilings and previews on: the plain path is bounded, the AppContainer roles are not.
    [Arguments("windows-jobs-previews", Limits | AppContainer, true, false)]
    // Windows with Job Object ceilings and previews off: the plain path is bounded, the floor roles run nowhere.
    [Arguments("windows-jobs", Limits, true, false)]
    // No ceiling mechanism: nothing, whatever the boundary.
    [Arguments("no-limits-bwrap", Bwrap, false, false)]
    [Arguments("no-limits-appcontainer", AppContainer, false, false)]
    public void CeilingsServed_IsThePlainPathFlag_AndOnlyABoundaryThatCarriesTheCeilings(string host,
        SandboxProviderCapabilities capabilities,
        bool plainServed,
        bool floorServed)
    {
        foreach (var name in Every)
        {
            var expected = Plain.Contains(name) ? plainServed : floorServed;
            AssertEx.Equal(expected, Declaration(name).CeilingsServed(capabilities), $"{name} on {host}");
        }
    }

    [Test]
    public void ARoleThatAsksForNoCeilings_IsNeverServed()
    {
        var none = SandboxWorkloads.AgentHome with { Ceilings = SandboxCeilingProfile.None };

        AssertEx.False(none.CeilingsServed(Limits | Bwrap | SandboxProviderCapabilities.SupportsNetworkPolicy));
    }

    private static SandboxRequirements Declaration(string name) =>
        (SandboxRequirements)typeof(SandboxWorkloads).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
}
