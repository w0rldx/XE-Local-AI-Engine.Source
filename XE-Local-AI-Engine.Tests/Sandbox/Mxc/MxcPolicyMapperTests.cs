namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using Microsoft.Mxc.Sdk.V1;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Field-by-field coverage of the one ProcessContainer policy <see cref="MxcPolicyMapper" /> builds.</summary>
[Category(TestCategories.Unit)]
public sealed class MxcPolicyMapperTests
{
    private static MxcLaunchRequest Request() => Request(TimeSpan.FromSeconds(30), environment: null);

    private static MxcLaunchRequest Request(TimeSpan? timeout, IReadOnlyDictionary<string, string>? environment) =>
        new()
        {
            Executable = @"C:\py\python.exe",
            Arguments = ["-c", "print('hi there')"],
            WorkingDirectory = @"C:\data\jails\j1\work",
            Environment = environment ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["SystemRoot"] = @"C:\Windows", ["PATH"] = @"C:\py" },
            JailRoot = @"C:\data\jails\j1",
            ReadOnlyTrees = [@"C:\data\runtime\python", @"C:\data\mcp"],
            DeniedRoots = [@"C:\Users\me", @"C:\data", @"C:\engine"],
            Timeout = timeout,
        };

    [Test]
    public async Task Build_ProducesAnEnforcingProcessContainer_WithNoGrantsBeyondThePolicy()
    {
        var request = MxcPolicyMapper.Build(Request());

        var container = request.Containment as Containment.ProcessContainer;
        AssertEx.NotNull(container);
        AssertEx.False(container!.LearningMode);
        AssertEx.Empty(container.Capabilities);
        AssertEx.Null(container.CaptureDenials);
        AssertEx.Null(container.Filesystem);
        AssertEx.Null(container.Network);
        var containerUi = AssertEx.NotNull(container.Ui);
        AssertEx.Equal(ProcessContainerUiIsolation.Container, containerUi.Isolation);
        AssertEx.False(containerUi.DesktopSystemControl);
        AssertEx.False(containerUi.Ime);
        AssertEx.Equal(ProcessContainerSystemSettings.None, containerUi.SystemSettings);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Build_MapsFilesystem_ToOneWritableJailReadOnlyTreesAndDeniedRoots()
    {
        var filesystem = AssertEx.NotNull(MxcPolicyMapper.Build(Request()).Filesystem);

        AssertEx.Equal(@"C:\data\jails\j1", AssertEx.NotNull(filesystem.ReadwritePaths).Single());
        AssertEx.Equal(2, filesystem.ReadonlyPaths.Count);
        AssertEx.Contains(filesystem.ReadonlyPaths, @"C:\data\runtime\python");
        AssertEx.Contains(filesystem.ReadonlyPaths, @"C:\data\mcp");
        AssertEx.Equal(3, filesystem.DeniedPaths.Count);
        AssertEx.Contains(filesystem.DeniedPaths, @"C:\Users\me");
        AssertEx.Contains(filesystem.DeniedPaths, @"C:\data");
        AssertEx.Contains(filesystem.DeniedPaths, @"C:\engine");
        AssertEx.True(filesystem.ClearPolicyOnExit == true);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Build_DeniesAllNetworkAndUi()
    {
        var request = MxcPolicyMapper.Build(Request());

        var network = AssertEx.NotNull(request.Network);
        AssertEx.Equal<NetworkAction?>(NetworkAction.Deny, AssertEx.NotNull(network.Egress).Default);
        AssertEx.Null(network.Egress!.Allow);
        AssertEx.Equal<NetworkAction?>(NetworkAction.Deny, AssertEx.NotNull(network.Ingress).Default);
        AssertEx.Equal<NetworkAction?>(NetworkAction.Deny, network.Ingress!.HostLoopback);
        AssertEx.Null(network.RuntimeConfig);
        var ui = AssertEx.NotNull(request.Ui);
        AssertEx.True(ui.Disable);
        AssertEx.Equal(ClipboardPolicy.None, ui.Clipboard);
        AssertEx.False(ui.AllowInputInjection);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Build_PassesEnvironmentVerbatim_AndNeverInheritsTheProfileBlock()
    {
        var request = MxcPolicyMapper.Build(Request());

        var environment = AssertEx.NotNull(request.Environment);
        AssertEx.Equal(2, environment.Count);
        AssertEx.Equal(@"C:\Windows", environment["SystemRoot"]);
        AssertEx.Equal(@"C:\py", environment["PATH"]);
        AssertEx.False(request.InheritDefaultEnvironment);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Build_MapsCommandWorkingDirectoryAndTimeout()
    {
        var request = MxcPolicyMapper.Build(Request());

        AssertEx.Equal(@"C:\py\python.exe -c ""print('hi there')""", request.Command);
        AssertEx.Equal(@"C:\data\jails\j1\work", request.WorkingDirectory);
        AssertEx.Equal<uint?>(30_000u, request.TimeoutMs);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Build_RoundsASubMillisecondTimeoutUp_SoItNeverBecomesMxcsNoTimeoutZero()
    {
        AssertEx.Equal<uint?>(1u, MxcPolicyMapper.Build(Request(TimeSpan.FromTicks(1), environment: null)).TimeoutMs);
        AssertEx.Null(MxcPolicyMapper.Build(Request(timeout: null, environment: null)).TimeoutMs);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => MxcPolicyMapper.Build(Request(TimeSpan.Zero, environment: null)));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("")]
    [Arguments("A=B")]
    public async Task Build_WhenAnEnvironmentNameIsInvalid_Throws(string name)
    {
        var request = Request(TimeSpan.FromSeconds(1), new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "x" });

        AssertEx.Throws<ArgumentException>(() => MxcPolicyMapper.Build(request));
        await Task.CompletedTask;
    }

    [Test]
    public async Task EveryBuiltRequest_PassesTheGuard()
    {
        AssertEx.DoesNotThrow(() => MxcPolicyGuard.Assert(MxcPolicyMapper.Build(Request())), "the launch request must pass the guard");
        AssertEx.DoesNotThrow(() => MxcPolicyGuard.Assert(MxcPolicyMapper.BuildProbeRequest()), "the probe request must pass the guard");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildProbeRequest_GrantsNoFilesystemPath()
    {
        var request = MxcPolicyMapper.BuildProbeRequest();

        AssertEx.Null(request.Filesystem);
        AssertEx.True(request.Containment is Containment.ProcessContainer);
        await Task.CompletedTask;
    }
}
