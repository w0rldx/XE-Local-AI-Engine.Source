namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The plain process path a host without the job ceilings takes, run against a real host shell.</summary>
[Category(TestCategories.Integration)]
public sealed class ProcessSandboxPlainPathTests
{
    /// <summary>A host whose probe failed advertises no ceilings: under <c>low</c> a plain command runs on the plain path, never the job launcher.</summary>
    [Test]
    public async Task AFailedProbe_UnderLow_RunsThePlainProcessPath()
    {
        var api = new FakeWindowsJobApi();
        using var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(new ProcessSandboxWindowsJobTests.FixedProbe(SandboxContainment.None)),
            new ProcessSandboxWindowsJobTests.CountingMarkerStore(),
            windowsJobLauncher: new WindowsJobLauncher(api));
        var ceilings = SandboxResourceCeilings.Resolve(SandboxWorkloads.AgentHome, provider.Capabilities, new ComputeOptions(), new LocalContainerOptions());
        AssertEx.Null(ceilings, "without the probe's flag no ceiling is asked for");
        AssertEx.False(SandboxSecurityProfilePolicy.Refuses(SandboxWorkloads.AgentHome, provider.Capabilities, SandboxSecurityProfile.Low, configRequiresEgressDenial: false));
        var handle = await provider.CreateOrAttachAsync(ProcessSandboxWindowsJobTests.Request() with { ResourceLimits = ceilings });
        var marker = Path.Combine(Path.GetTempPath(), "xe-job-plain-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await provider.ExecuteAsync(handle, ProcessSandboxWindowsJobTests.WritesMarker("plain", marker));

            AssertEx.True(result.Completed, result.StandardError);
            AssertEx.Equal(0, result.ExitCode, result.StandardError);
            AssertEx.True(File.Exists(marker), "the plain command ran");
            AssertEx.Empty(api.Calls);
        }
        finally
        {
            File.Delete(marker);
            await provider.KillAsync(handle);
        }
    }
}
