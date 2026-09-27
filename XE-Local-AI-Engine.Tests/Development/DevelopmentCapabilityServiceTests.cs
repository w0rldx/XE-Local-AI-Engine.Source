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

    private static DevelopmentCapabilityService CreateService(IDockerDaemonPreflightService preflight, string home)
    {
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        var probe = Substitute.For<ISandboxContainmentProbe>();
        probe.Containment.Returns(SandboxContainment.None);
        return new DevelopmentCapabilityService(Options.Create(new DevelopmentOptions()),
            Options.Create(new SandboxOptions()),
            Options.Create(new DevelopmentSandboxOptions()),
            provider,
            provider,
            provider,
            probe,
            preflight,
            () => home);
    }
}
