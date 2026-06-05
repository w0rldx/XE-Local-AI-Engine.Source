namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Xml.Linq;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentWindowsScaffoldTests
{
    private static readonly string ProjectRoot = GetProjectRoot();

    [Test]
    public async Task Project_WhenScaffolded_IsRegisteredAsSingleFileWindowsExecutable()
    {
        var solution = await File.ReadAllTextAsync(Path.Combine(ProjectRoot, "Apps", "XE-Local-AI-Engine", "XE-Local-AI-Engine.slnx"));
        var projectPath = GetWindowsProjectPath("XE-Local-AI-Engine.HostAgent.Windows.csproj");
        var project = XDocument.Load(projectPath);

        AssertEx.Contains(solution, "XE-Local-AI-Engine.HostAgent.Windows/XE-Local-AI-Engine.HostAgent.Windows.csproj");
        AssertEx.Equal("Exe", GetProperty(project, "OutputType"));
        AssertEx.Equal("win-x64", GetProperty(project, "RuntimeIdentifier"));
        AssertEx.Equal("true", GetProperty(project, "SelfContained"));
        AssertEx.Equal("true", GetProperty(project, "PublishSingleFile"));
        AssertEx.Equal("XE-Local-AI-Engine.HostAgent.Windows", GetProperty(project, "AssemblyName"));
    }

    [Test]
    public async Task Packaging_WhenScaffolded_UsesFileCopyAndShortcutOnly()
    {
        var packagingDirectory = GetWindowsProjectPath("Packaging", "Windows");
        var scripts = Directory.EnumerateFiles(packagingDirectory, "*.ps1", SearchOption.TopDirectoryOnly);

        foreach (var scriptPath in scripts)
        {
            var script = await File.ReadAllTextAsync(scriptPath);

            AssertEx.Contains(script, "XE-Local-AI-Engine.HostAgent.Windows.exe");
            AssertEx.False(ContainsExecutableLine(script, "sc.exe"));
            AssertEx.False(ContainsExecutableLine(script, "New-Service"));
            AssertEx.False(ContainsExecutableLine(script, "schtasks"));
            AssertEx.False(ContainsExecutableLine(script, "Register-ScheduledTask"));
            AssertEx.False(ContainsExecutableLine(script, "Set-ItemProperty"));
            AssertEx.False(ContainsExecutableLine(script, "New-ItemProperty"));
        }
    }

    [Test]
    public async Task Packaging_WhenF4Implemented_CreatesTrayShortcutsWithoutAutostart()
    {
        var installScript = await File.ReadAllTextAsync(GetWindowsProjectPath("Packaging", "Windows", "install-host-agent.ps1"));
        var uninstallScript = await File.ReadAllTextAsync(GetWindowsProjectPath("Packaging", "Windows", "uninstall-host-agent.ps1"));

        AssertEx.Contains(installScript, "XE-Local-AI-Engine.Tray.exe");
        AssertEx.Contains(installScript, "CommonDesktopDirectory");
        AssertEx.Contains(installScript, "XE-Local-AI-Engine.lnk");
        AssertEx.Contains(installScript, "XE-Local-AI-Engine — Log Mode.lnk");
        AssertEx.Contains(installScript, "--log");
        AssertEx.Contains(uninstallScript, "XE-Local-AI-Engine.Tray.exe");
        AssertEx.Contains(uninstallScript, "XE-Local-AI-Engine — Log Mode.lnk");
        AssertEx.False(ContainsExecutableLine(installScript, "schtasks"));
        AssertEx.False(ContainsExecutableLine(installScript, "Register-ScheduledTask"));
        AssertEx.False(ContainsExecutableLine(installScript, "Set-ItemProperty"));
        AssertEx.False(ContainsExecutableLine(installScript, "New-ItemProperty"));
    }

    [Test]
    public async Task Program_WhenScaffolded_LogsToRotatingFileWithoutConsoleProvider()
    {
        var program = await File.ReadAllTextAsync(GetWindowsProjectPath("Program.cs"));
        var loggerProvider = await File.ReadAllTextAsync(GetWindowsProjectPath("Implementation", "RotatingFileLoggerProvider.cs"));

        AssertEx.Contains(program, "builder.Logging.ClearProviders()");
        AssertEx.Contains(program, "RotatingFileLoggerProvider");
        AssertEx.Contains(loggerProvider, "host-agent-{date:yyyyMMdd}-{sequence:D3}.log");
    }

    [Test]
    public async Task ConsoleControlHandler_WhenScaffolded_IsWindowsOnlyDeveloperPath()
    {
        var handler = await File.ReadAllTextAsync(GetWindowsProjectPath("WindowsConsoleControlHandler.cs"));

        AssertEx.Contains(handler, "OperatingSystem.IsWindows()");
        AssertEx.Contains(handler, "GetConsoleWindow() == IntPtr.Zero");
        AssertEx.Contains(handler, "SetConsoleCtrlHandler");
        AssertEx.Contains(handler, "applicationLifetime.StopApplication()");
    }

    [Test]
    public async Task SecretStorage_WhenScaffolded_UsesCurrentUserDpapi()
    {
        var protector = await File.ReadAllTextAsync(GetWindowsProjectPath("Implementation", "DpapiCurrentUserSecretProtector.cs"));

        AssertEx.Contains(protector, "ProtectedData.Protect");
        AssertEx.Contains(protector, "ProtectedData.Unprotect");
        AssertEx.Contains(protector, "DataProtectionScope.CurrentUser");
        AssertEx.False(protector.Contains("DataProtectionScope.LocalMachine", StringComparison.Ordinal));
    }

    [Test]
    public async Task SecretStorage_WhenScaffolded_GrantsExpectedWindowsPrincipalsOnly()
    {
        var acl = await File.ReadAllTextAsync(GetWindowsProjectPath("Implementation", "WindowsHostAgentAcl.cs"));
        var identity = await File.ReadAllTextAsync(GetWindowsProjectPath("WindowsHostIdentity.cs"));
        var paths = await File.ReadAllTextAsync(GetWindowsProjectPath("HostAgentWindowsPaths.cs"));

        AssertEx.Contains(paths, "admin-token.dpapi");
        AssertEx.Contains(identity, "BuiltinAdministratorsSid");
        AssertEx.Contains(identity, "LocalSystemSid");
        AssertEx.Contains(acl, "identity.UserSid");
        AssertEx.Contains(acl, "identity.AdministratorsSid");
        AssertEx.Contains(acl, "identity.SystemSid");
        AssertEx.Contains(acl, "FileSystemRights.FullControl");
        AssertEx.Contains(acl, "SetAccessRuleProtection(isProtected: true, preserveInheritance: false)");
    }

    [Test]
    public async Task SecretStorage_WhenScaffolded_DoesNotLogTokenMaterial()
    {
        var secretStore = await File.ReadAllTextAsync(GetWindowsProjectPath("Implementation", "HostAgentSecretStore.cs"));
        var initializer = await File.ReadAllTextAsync(GetWindowsProjectPath("Implementation", "AdminTokenInitializationHostedService.cs"));

        AssertEx.False(secretStore.Contains("ILogger", StringComparison.Ordinal));
        AssertEx.False(initializer.Contains("{token", StringComparison.OrdinalIgnoreCase));
        AssertEx.Contains(initializer, "admin token secret storage initialized");
    }

    [Test]
    public async Task WslDriver_WhenScaffolded_UsesAllowlistAndHostOwnedTerminateBoundary()
    {
        var driver = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "Implementation", "Wsl2Driver.cs"));
        var allowlist = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "WslCommandAllowlist.cs"));
        var runner = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "Implementation", "WindowsProcessRunner.cs"));
        var linuxInstallScript = await File.ReadAllTextAsync(Path.Combine(ProjectRoot,
            "Apps",
            "XE-Local-AI-Engine",
            "XE-Local-AI-Engine.HostAgent.Linux",
            "Packaging",
            "install-user-unit.sh"));

        AssertEx.Contains(driver, "VerifyScriptHash");
        AssertEx.Contains(driver, "RunPhaseBoundaryAsync");
        AssertEx.Contains(driver, "TerminateAsync");
        AssertEx.Contains(driver, "SystemIsRunning");
        AssertEx.Contains(driver, "InitVersion");
        AssertEx.Contains(allowlist, "AllowedPatterns");
        AssertEx.Contains(allowlist, "WslArgumentNotAllowedException");
        AssertEx.Contains(runner, "startInfo.ArgumentList.Add(argument)");
        AssertEx.Contains(runner, "UseShellExecute = false");
        AssertEx.False(linuxInstallScript.Contains("wsl --terminate", StringComparison.Ordinal));
    }

    private static string GetProperty(XDocument project, string name)
    {
        return project.Root?
                      .Elements("PropertyGroup")
                      .Elements(name)
                      .Select(static element => element.Value)
                      .FirstOrDefault()
               ?? throw new InvalidOperationException($"Missing project property {name}.");
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

    private static bool ContainsExecutableLine(string script, string commandPrefix)
    {
        return script.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                     .Any(line => line.StartsWith(commandPrefix, StringComparison.OrdinalIgnoreCase));
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
