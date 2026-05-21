namespace XE_Local_AI_Engine.Tests.HostAgent;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class TrayDetachedProcessSourceTests
{
    private static readonly string ProjectRoot = GetProjectRoot();

    [Test]
    public async Task WindowsLauncher_WhenD1bImplemented_UsesCreateProcessWDetachedFlagsAndNoHandleInheritance()
    {
        var launcher = await File.ReadAllTextAsync(GetTrayProjectPath("WindowsDetachedProcessLauncher.cs"));
        var app = await File.ReadAllTextAsync(GetTrayProjectPath("App.axaml.cs"));

        AssertEx.Contains(app, "WindowsDetachedProcessLauncher.StartDetached");
        AssertEx.Contains(launcher, "EntryPoint = \"CreateProcessW\"");
        AssertEx.Contains(launcher, "DetachedProcess = 0x00000008");
        AssertEx.Contains(launcher, "CreateNewProcessGroup = 0x00000200");
        AssertEx.Contains(launcher, "CreateBreakawayFromJob = 0x01000000");
        AssertEx.Contains(launcher, "inheritHandles: false");
        AssertEx.Contains(launcher, "processAttributes: IntPtr.Zero");
        AssertEx.Contains(launcher, "threadAttributes: IntPtr.Zero");
        AssertEx.Contains(launcher, "environment: IntPtr.Zero");
        AssertEx.Contains(launcher, "InvalidHandleValue = new(-1)");
        AssertEx.Contains(launcher, "StandardInput = NativeMethods.InvalidHandleValue");
        AssertEx.Contains(launcher, "StandardOutput = NativeMethods.InvalidHandleValue");
        AssertEx.Contains(launcher, "StandardError = NativeMethods.InvalidHandleValue");
        AssertEx.Contains(launcher, "CloseHandle(processInformation.Thread)");
        AssertEx.Contains(launcher, "CloseHandle(processInformation.Process)");
        AssertEx.False(app.Contains("Process.Start(startInfo)", StringComparison.Ordinal));
    }

    private static string GetTrayProjectPath(params string[] relativePath)
    {
        return Path.Combine([
            ProjectRoot,
            "Apps",
            "XE-Local-AI-Engine",
            "XE-Local-AI-Engine.Tray",
            .. relativePath
        ]);
    }

    private static string GetProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "C0re.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Unable to locate repository root.");
    }
}
