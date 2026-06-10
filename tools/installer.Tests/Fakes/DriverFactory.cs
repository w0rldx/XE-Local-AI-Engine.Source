namespace XE_Local_AI_Engine.Installer.Tests.Fakes;

using NSubstitute;

using XE_Local_AI_Engine.Installer.Cli;
using XE_Local_AI_Engine.Installer.Driver;
using XE_Local_AI_Engine.Installer.StateMachine;

/// <summary>Builds a fully-mocked <see cref="IInstallerEnvironmentDriver" /> wired for a happy-path run.</summary>
internal static class DriverFactory
{
    public const string DistroName = "xe-engine-runtime";
    public const string BootstrapModel = "qwen3:0.6b";
    public const long RequiredDiskBytes = 12L * 1024 * 1024 * 1024;

    public static IInstallerEnvironmentDriver CreateHappyPath()
    {
        var driver = Substitute.For<IInstallerEnvironmentDriver>();

        driver.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WslProbeResult
            {
                WslFeaturePresent = true,
                Wsl2Capable = true,
                DistroPresent = false,
                FreeDiskBytes = RequiredDiskBytes * 2,
                RequiredFreeDiskBytes = RequiredDiskBytes
            });

        driver.LoadImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("expected-image-id");
        driver.PullModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(BootstrapModel);
        // Phases are not already-satisfied by default → they run; inventory is empty by default.
        driver.IsPhaseSatisfiedAsync(Arg.Any<InstallerPhaseProbe>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        driver.BuildTeardownInventoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        driver.TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new TeardownResult
            {
                DistroRemoved = true,
                ProgramDataRemoved = true,
                ManifestRemoved = true,
                Residuals = []
            });

        return driver;
    }

    public static InstallContext CreateContext(string bundlePath = "/fixture/bundle") => new()
    {
        BundlePath = bundlePath,
        InstallerVersion = "0.1.0-rc.1",
        DistroName = DistroName,
        BootstrapModel = BootstrapModel,
        MinimumFreeDiskBytes = RequiredDiskBytes
    };
}
