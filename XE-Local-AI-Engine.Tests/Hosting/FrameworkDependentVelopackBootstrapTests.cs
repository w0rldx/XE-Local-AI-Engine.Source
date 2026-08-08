namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class FrameworkDependentVelopackBootstrapTests
{
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
