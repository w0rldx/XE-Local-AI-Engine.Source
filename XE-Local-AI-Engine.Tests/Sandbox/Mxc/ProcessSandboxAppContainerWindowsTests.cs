namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The Windows path end to end: real probe, a boundary-served sandbox, and a <c>cmd.exe</c> through
///     <see cref="ProcessSandboxRuntimeProvider" /> that writes its jail but cannot read a canary outside it.
/// </summary>
/// <remarks>Skips with the probe's own reason (unsupported build, missing wxc-host-prep) and never passes silently.</remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows10.0.26100.0")]
public sealed class ProcessSandboxAppContainerWindowsTests
{
    [Test]
    public async Task CmdThroughTheProvider_WritesItsJail_AndCannotReadACanaryOutsideIt()
    {
        var launcher = new SandboxLauncher(new HostSandboxContainmentProbe());
        var containment = launcher.Containment;
        Skip.Unless(containment.SupportsAppContainerBoundary, $"the AppContainer boundary is unavailable here: {containment.AppContainerBoundaryUnavailableReason}");

        var outside = Directory.CreateTempSubdirectory("xe-mxc-canary-").FullName;
        var canary = Path.Combine(outside, "canary.txt");
        await File.WriteAllTextAsync(canary, "provider-canary-secret");
        using var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            TimeProvider.System,
            logger: null,
            launcher,
            previewPolicy: new PreviewsOn());
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "mxc-windows-" + Guid.NewGuid().ToString("N"),
                ManifestVersion = 1
            },
            RuntimeProfile = "compute",
            NetworkPolicy = SandboxNetworkPolicy.None,
            Isolation = SandboxIsolationMode.Filesystem
        });
        try
        {
            var jail = AssertEx.NotNull(handle.WorkingRoot);
            AssertEx.Equal(SandboxIsolatedPaths.ForHostJail(jail), handle.IsolatedPaths);

            var write = await provider.ExecuteAsync(handle, Cmd("write", "echo", "inside>", Path.Combine(jail, "out.txt")));
            AssertEx.Equal(0, write.ExitCode, $"positive control failed: {write.StandardError}");
            AssertEx.Equal("inside", (await File.ReadAllTextAsync(Path.Combine(jail, "out.txt"))).Trim());

            var read = await provider.ExecuteAsync(handle, Cmd("read", "type", canary));
            AssertEx.NotEqual(0, read.ExitCode, $"the canary outside the jail was readable: {read.StandardOutput}");
            AssertEx.False(read.StandardOutput.Contains("provider-canary-secret", StringComparison.Ordinal));
        }
        finally
        {
            await provider.KillAsync(handle);
            Directory.Delete(outside, recursive: true);
        }
    }

    // Separate arguments, as the MXC runtime test passes them: cmd.exe does not read backslash-escaped quotes inside a /c string.
    private static SandboxCommandRequest Cmd(string id, params string[] arguments) =>
        new()
        {
            ExecutionId = "mxc-windows-" + id,
            Executable = "cmd.exe",
            Arguments = ["/c", .. arguments],
            Timeout = TimeSpan.FromSeconds(60)
        };

    private sealed class PreviewsOn : IExecutionPreviewPolicy
    {
        public bool PreviewMechanismsEnabled => true;
    }
}
