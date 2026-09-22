namespace XE_Local_AI_Engine.Tests.Hosting;

using NSubstitute;
using Velopack.Locators;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class FrameworkDependentVelopackBootstrapTests
{
    [Test]
    public void CreateSupervisedProcess_RetainsMountedEnginePathButWaitsForShell()
    {
        var process = Substitute.For<IProcessImpl>();
        process.GetCurrentProcessId().Returns(5678u);
        process.GetCurrentProcessPath().Returns("/tmp/.mount_xe/usr/bin/XE-Local-AI-Engine.Client");

        var supervised = FrameworkDependentVelopackBootstrap.CreateSupervisedProcess(process, "1234");

        AssertEx.Equal(expected: 1234u, supervised.GetCurrentProcessId());
        AssertEx.Equal("/tmp/.mount_xe/usr/bin/XE-Local-AI-Engine.Client", supervised.GetCurrentProcessPath());
        supervised.Exit(3);
        process.Received(1).Exit(3);
    }

    [Test]
    [Arguments(null)]
    [Arguments("invalid")]
    [Arguments("0")]
    public void CreateSupervisedProcess_StandaloneOrInvalidIdentityKeepsEngine(string? supervisorId)
    {
        var process = Substitute.For<IProcessImpl>();
        process.GetCurrentProcessId().Returns(5678u);
        AssertEx.True(ReferenceEquals(process, FrameworkDependentVelopackBootstrap.CreateSupervisedProcess(process, supervisorId)));
    }

    [Test]
    public void ResolveLauncherPath_WindowsDotnetHostWithAdjacentLauncher_ReturnsLauncher()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "xe-launcher-tests", "current");
        var launcher = Path.Combine(baseDirectory, FrameworkDependentVelopackBootstrap.WindowsLauncherFileName);

        var resolved = FrameworkDependentVelopackBootstrap.ResolveLauncherPath(isWindows: true,
            processPath: Path.Combine("C:\\Program Files\\dotnet", "dotnet.exe"),
            baseDirectory,
            _ => true);

        AssertEx.Equal(launcher, resolved);
    }

    [Test]
    public void ResolveLauncherPath_NativeOrUnpackagedProcess_DoesNotOverrideVelopack()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "xe-launcher-tests", "current");

        AssertEx.Null(FrameworkDependentVelopackBootstrap.ResolveLauncherPath(isWindows: false,
            processPath: "/usr/bin/dotnet",
            baseDirectory,
            _ => true));
        AssertEx.Null(FrameworkDependentVelopackBootstrap.ResolveLauncherPath(isWindows: true,
            processPath: Path.Combine(baseDirectory, FrameworkDependentVelopackBootstrap.WindowsLauncherFileName),
            baseDirectory,
            _ => true));
        AssertEx.Null(FrameworkDependentVelopackBootstrap.ResolveLauncherPath(isWindows: true,
            processPath: "C:\\Program Files\\dotnet\\dotnet.exe",
            baseDirectory,
            _ => false));
    }

    [Test]
    public void ResolveLauncherProcessId_UsesTheParentLauncherSoVelopackWaitsForItsExecutableToExit()
    {
        AssertEx.Equal(expected: 1234u, FrameworkDependentVelopackBootstrap.ResolveLauncherProcessId("1234", 5678));
        AssertEx.Equal(expected: 5678u, FrameworkDependentVelopackBootstrap.ResolveLauncherProcessId("invalid", 5678));
        AssertEx.Equal(expected: 5678u, FrameworkDependentVelopackBootstrap.ResolveLauncherProcessId("0", 5678));
    }
}
