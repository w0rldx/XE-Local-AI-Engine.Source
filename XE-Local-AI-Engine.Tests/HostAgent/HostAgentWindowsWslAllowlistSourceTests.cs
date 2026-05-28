namespace XE_Local_AI_Engine.Tests.HostAgent;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentWindowsWslAllowlistSourceTests
{
    private static readonly string ProjectRoot = GetProjectRoot();

    [Test]
    public async Task Allowlist_WhenScaffolded_ContainsAllApprovedPlanForms()
    {
        var allowlist = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "WslCommandAllowlist.cs"));

        AssertEx.Contains(allowlist, "ListRunningQuiet");
        AssertEx.Contains(allowlist, "ListVerbose");
        AssertEx.Contains(allowlist, "Status");
        AssertEx.Contains(allowlist, "InstallNoDistribution");
        AssertEx.Contains(allowlist, "Import");
        AssertEx.Contains(allowlist, "Unregister");
        AssertEx.Contains(allowlist, "Terminate");
        AssertEx.Contains(allowlist, "Shutdown");
        AssertEx.Contains(allowlist, "BootstrapScript");
        AssertEx.Contains(allowlist, "RuntimeInstallScript");
        AssertEx.Contains(allowlist, "Wake");
        AssertEx.Contains(allowlist, "UserSystemctl");
        AssertEx.Contains(allowlist, "HostAgentCtl");
        AssertEx.Contains(allowlist, "SystemIsRunning");
        AssertEx.Contains(allowlist, "InitVersion");
    }

    [Test]
    public async Task Allowlist_WhenScaffolded_RejectsInvalidDistroNamesAndNonRootedImportPaths()
    {
        var allowlist = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "WslCommandAllowlist.cs"));

        AssertEx.Contains(allowlist, "WslArgumentNotAllowedException");
        AssertEx.Contains(allowlist, "^[A-Za-z0-9_.-]+$");
        AssertEx.Contains(allowlist, "Path.IsPathRooted(args[2])");
        AssertEx.Contains(allowlist, "Path.IsPathRooted(args[3])");
    }

    [Test]
    public async Task Allowlist_WhenScaffolded_RestrictsSystemctlAndCtlVerbs()
    {
        var allowlist = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "WslCommandAllowlist.cs"));

        AssertEx.Contains(allowlist, "start");
        AssertEx.Contains(allowlist, "restart");
        AssertEx.Contains(allowlist, "stop");
        AssertEx.Contains(allowlist, "is-active");
        AssertEx.Contains(allowlist, "status");
        AssertEx.Contains(allowlist, "reload");
        AssertEx.Contains(allowlist, "read-phase-exit");
        AssertEx.False(allowlist.Contains("enable", StringComparison.Ordinal));
        AssertEx.False(allowlist.Contains("cat", StringComparison.Ordinal));
    }

    [Test]
    public async Task ProcessRunner_WhenScaffolded_UsesArgumentListAndNoShellInterpolation()
    {
        var runner = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "Implementation", "WindowsProcessRunner.cs"));

        AssertEx.Contains(runner, "UseShellExecute = false");
        AssertEx.Contains(runner, "startInfo.ArgumentList.Add(argument)");
        AssertEx.Contains(runner, "RedirectStandardOutput = true");
        AssertEx.Contains(runner, "RedirectStandardError = true");
        AssertEx.Contains(runner, "MaxCapturedCharacters");
        AssertEx.False(runner.Contains("Arguments =", StringComparison.Ordinal));
    }

    private static string GetWindowsProjectPath(params string[] relativePath)
    {
        var segments = new[]
        {
            ProjectRoot,
            "Apps",
            "XE-Local-AI-Engine",
            "XE-Local-AI-Engine.HostAgent.Windows"
        }.Concat(relativePath).ToArray();

        return Path.Combine(segments);
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
