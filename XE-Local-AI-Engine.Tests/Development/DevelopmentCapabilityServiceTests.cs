namespace XE_Local_AI_Engine.Tests.Development;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     <see cref="DevelopmentCapabilityService" />: the home-directory caveat is driven through the seam rather than by
///     unsetting HOME, and a non-container provider never reaches the Docker preflight.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DevelopmentCapabilityServiceTests
{
    [Test]
    public async Task GetAsync_WithoutAHomeDirectory_WithholdsTheBoundaryOnTheMcpStdioRowOnly()
    {
        var preflight = Substitute.For<IDockerDaemonPreflightService>();
        var service = CreateService(preflight, home: string.Empty);

        var capability = await service.GetAsync();

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, capability.SandboxProvider);
        AssertEx.Null(capability.ContainerRuntime);
        await preflight.DidNotReceiveWithAnyArgs().InspectAsync(default);
        AssertEx.Equal("agent-home,run_python,mcp-stdio,development,work-session", string.Join(',', capability.Isolation.Select(static row => row.Role)));
        AssertEx.Equal("mcp-stdio", string.Join(',', capability.Isolation.Where(static row => row.FilesystemIsolationWithheldReason is not null).Select(static row => row.Role)));
        AssertEx.Equal(DevelopmentCapabilityService.NoHomeDirectoryReason, capability.Isolation[2].FilesystemIsolationWithheldReason);

        var mcp = capability.ToResponse().Isolation.Single(static row => row.Role == "mcp-stdio");
        AssertEx.False(mcp.FilesystemIsolation);
        AssertEx.Equal(DevelopmentCapabilityService.NoHomeDirectoryReason, mcp.FilesystemIsolationUnavailableReason);
    }

    [Test]
    public async Task GetAsync_WithAHomeDirectory_WithholdsNothing()
    {
        var service = CreateService(Substitute.For<IDockerDaemonPreflightService>(), home: "/home/operator");

        var capability = await service.GetAsync();

        AssertEx.True(capability.Isolation.All(static row => row.FilesystemIsolationWithheldReason is null));
    }

    /// <summary>
    ///     The capability carries the profile in effect and the roles <c>high</c> WOULD refuse here whatever is in effect, from the rule the
    ///     create sites enforce (ADR 0020).
    /// </summary>
    /// <remarks>
    ///     The deterministic fake serves no egress denial, no ceilings and no host-filesystem boundary: the three roles without a Filesystem
    ///     floor are listed, while run_python and mcp-stdio are refused by their floor under every profile and are not blamed on high.
    /// </remarks>
    [Test]
    [Arguments(SandboxSecurityProfile.Low, "low")]
    [Arguments(SandboxSecurityProfile.High, "high")]
    public async Task GetAsync_ReportsTheProfileInEffect_AndTheRolesOnlyHighWouldRefuseOnThisHost(SandboxSecurityProfile profile, string literal)
    {
        var service = CreateService(Substitute.For<IDockerDaemonPreflightService>(), home: "/home/operator", profile);

        var response = (await service.GetAsync()).ToResponse();

        AssertEx.Equal(literal, response.SandboxSecurityProfile);
        AssertEx.Equal("agent-home,development,work-session", string.Join(',', response.HighProfileRefusals));
        AssertEx.Equal(profile == SandboxSecurityProfile.High,
            response.Isolation.Single(static row => row.Role == "agent-home").ResourceLimitsRequired);
    }

    // AgentHome's config key set on a backend that cannot deny egress: AgentHome and work sessions are refused under low already, so
    // high adds nothing for them and they must not be listed; the one floor-free role high still adds ceilings to stays listed.
    [Test]
    public async Task GetAsync_DoesNotBlameHighForARoleTheConfigKeyAlreadyRefusesUnderLow()
    {
        var service = CreateService(Substitute.For<IDockerDaemonPreflightService>(), home: "/home/operator", agentKeyRequiresEgressDenial: true);

        var response = (await service.GetAsync()).ToResponse();

        AssertEx.Equal("development", string.Join(',', response.HighProfileRefusals));
    }

    /// <summary>Off Linux the node never offers <c>run_python</c>, so the isolation summary must not report a role nothing can run.</summary>
    [Test]
    public async Task GetAsync_OnAHostThatCannotOfferRunPython_OmitsItsRow()
    {
        var service = CreateService(Substitute.For<IDockerDaemonPreflightService>(), "/home/operator", offersRunPython: false);

        var capability = await service.GetAsync();

        AssertEx.Equal("agent-home,mcp-stdio,development,work-session", string.Join(',', capability.Isolation.Select(static row => row.Role)));
    }

    // A backend that serves the Filesystem floor but no ceilings: the floor roles now run under low and are listed, except mcp-stdio while its
    // boundary is withheld for want of a home directory, which refuses every Sandboxed connection whatever the profile.
    [Test]
    [Arguments("/home/operator", "agent-home,run_python,mcp-stdio,development,work-session")]
    [Arguments("", "agent-home,run_python,development,work-session")]
    public async Task GetAsync_ListsAFloorRoleOnlyWhereItsFloorIsServedAndItsBoundaryIsNotWithheld(string home, string expected)
    {
        var service = CreateService(Substitute.For<IDockerDaemonPreflightService>(),
            home,
            capabilities: new FakeSandboxRuntimeProvider(TimeProvider.System).Capabilities | SandboxProviderCapabilities.SupportsHostFilesystemBoundary);

        var response = (await service.GetAsync()).ToResponse();

        AssertEx.Equal(expected, string.Join(',', response.HighProfileRefusals));
    }

    private static DevelopmentCapabilityService CreateService(IDockerDaemonPreflightService preflight,
        string home,
        SandboxSecurityProfile profile = SandboxSecurityProfile.Low,
        bool agentKeyRequiresEgressDenial = false,
        SandboxProviderCapabilities? capabilities = null,
        bool offersRunPython = true)
    {
        var fake = new FakeSandboxRuntimeProvider(TimeProvider.System);
        var probe = Substitute.For<ISandboxContainmentProbe>();
        probe.Containment.Returns(SandboxContainment.None);
        return new DevelopmentCapabilityService(Options.Create(new DevelopmentOptions()),
            Options.Create(new SandboxOptions
            {
                RequireEgressDenial = agentKeyRequiresEgressDenial
            }),
            Options.Create(new DevelopmentSandboxOptions()),
            capabilities is null ? fake : Advertising<IDevelopmentSandboxRuntimeProvider>(fake.ProviderName, capabilities.Value),
            capabilities is null ? fake : Advertising<IAgentSandboxRuntimeProvider>(fake.ProviderName, capabilities.Value),
            capabilities is null ? fake : Advertising<IWorkSessionSandboxRuntimeProvider>(fake.ProviderName, capabilities.Value),
            probe,
            preflight,
            StubNodeRuntimeSettings.Create().WithSandboxSecurityProfile(profile).Build(),
            () => home,
            offersRunPython);
    }

    // The capability projection reads only the name and the advertised flags, so a substitute advertising a chosen set stands in for a backend.
    private static TProvider Advertising<TProvider>(string providerName, SandboxProviderCapabilities capabilities)
        where TProvider : class, ISandboxRuntimeProvider
    {
        var provider = Substitute.For<TProvider>();
        _ = provider.ProviderName.Returns(providerName);
        _ = provider.Capabilities.Returns(capabilities);
        return provider;
    }
}
