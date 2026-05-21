namespace XE_Local_AI_Engine.Tests.HostAgent;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class TrayMenuSourceTests
{
    private static readonly string ProjectRoot = GetProjectRoot();

    [Test]
    public async Task TrayMenu_WhenF3Implemented_ExposesRequiredActions()
    {
        var xaml = await File.ReadAllTextAsync(GetTrayProjectPath("App.axaml"));

        AssertEx.Contains(xaml, "Header=\"Open Web UI\"");
        AssertEx.Contains(xaml, "Click=\"OpenWebUiMenuItemOnClick\"");
        AssertEx.Contains(xaml, "Header=\"Start HostAgent\"");
        AssertEx.Contains(xaml, "Click=\"StartHostAgentMenuItemOnClick\"");
        AssertEx.Contains(xaml, "Header=\"Stop Services\"");
        AssertEx.Contains(xaml, "Click=\"StopServicesMenuItemOnClick\"");
        AssertEx.Contains(xaml, "Header=\"Start Services\"");
        AssertEx.Contains(xaml, "Click=\"StartServicesMenuItemOnClick\"");
        AssertEx.Contains(xaml, "Header=\"Restart Runtime\"");
        AssertEx.Contains(xaml, "Click=\"RestartRuntimeMenuItemOnClick\"");
        AssertEx.Contains(xaml, "Header=\"Show Diagnostics\"");
        AssertEx.Contains(xaml, "Click=\"ShowDiagnosticsMenuItemOnClick\"");
        AssertEx.Contains(xaml, "Header=\"Quit Tray\"");
    }

    [Test]
    public async Task TrayMenu_WhenF3Implemented_UsesStatusDrivenVisibilityAndPlatformLaunchers()
    {
        var app = await File.ReadAllTextAsync(GetTrayProjectPath("App.axaml.cs"));
        var snapshot = await File.ReadAllTextAsync(GetTrayProjectPath("TrayHealthSnapshot.cs"));

        AssertEx.Contains(app, "_openWebUiMenuItem.IsEnabled = snapshot.IsReachable");
        AssertEx.Contains(app, "_stopServicesMenuItem.IsVisible = snapshot.IsReachable && snapshot.IsDesiredStateRunning");
        AssertEx.Contains(app, "_startServicesMenuItem.IsVisible = snapshot.IsReachable && snapshot.IsDesiredStateStopped");
        AssertEx.Contains(app, "_restartRuntimeMenuItem.IsVisible = snapshot.IsReachable && snapshot.IsDesiredStateRunning");
        AssertEx.Contains(app, "Process.Start(\"explorer.exe\", uri.AbsoluteUri)");
        AssertEx.Contains(app, "Process.Start(\"xdg-open\", uri.AbsoluteUri)");
        AssertEx.Contains(snapshot, "IsDesiredStateRunning");
        AssertEx.Contains(snapshot, "IsDesiredStateStopped");
    }

    [Test]
    public async Task TrayMenu_WhenF5Implemented_ShowsStartHostAgentForUnreachableRedState()
    {
        var app = await File.ReadAllTextAsync(GetTrayProjectPath("App.axaml.cs"));
        var snapshot = await File.ReadAllTextAsync(GetTrayProjectPath("TrayHealthSnapshot.cs"));

        AssertEx.Contains(app, "_startHostAgentMenuItem.IsVisible = !snapshot.IsReachable");
        AssertEx.Contains(app, "StartHostAgentAsync");
        AssertEx.Contains(app, "XE-Local-AI-Engine.HostAgent.Windows.exe");
        AssertEx.Contains(app, "systemctl");
        AssertEx.Contains(app, "--user");
        AssertEx.Contains(app, "start");
        AssertEx.Contains(app, "xe-host-agent.service");
        AssertEx.Contains(snapshot, "tray-red.ico");
        AssertEx.Contains(snapshot, "HostAgent unreachable");
    }

    [Test]
    public async Task TrayLifecycleActions_WhenF3Implemented_ConfirmAndUseBearerTokenWithoutLogging()
    {
        var app = await File.ReadAllTextAsync(GetTrayProjectPath("App.axaml.cs"));
        var client = await File.ReadAllTextAsync(GetTrayProjectPath("HostAgentStatusClient.cs"));
        var tokenStore = await File.ReadAllTextAsync(GetTrayProjectPath("HostAgentAdminTokenStore.cs"));

        AssertEx.Contains(app, "ShowConfirmationAsync");
        AssertEx.Contains(app, "endpointName: \"shutdown\"");
        AssertEx.Contains(app, "endpointName: \"startup\"");
        AssertEx.Contains(app, "endpointName: \"restart\"");
        AssertEx.Contains(client, "AuthenticationHeaderValue(\"Bearer\", token)");
        AssertEx.Contains(client, "response.StatusCode != HttpStatusCode.Unauthorized");
        AssertEx.Contains(tokenStore, "admin-token.dpapi");
        AssertEx.Contains(tokenStore, "ProtectedData.Unprotect");
        AssertEx.Contains(tokenStore, "XDG_RUNTIME_DIR");
        AssertEx.Contains(tokenStore, "admin-token");
        AssertEx.False(app.Contains("ILogger", StringComparison.Ordinal));
        AssertEx.False(client.Contains("LogInformation", StringComparison.Ordinal));
        AssertEx.False(tokenStore.Contains("Console.Write", StringComparison.Ordinal));
    }

    [Test]
    public async Task TrayRuntimeMetadata_WhenReattaching_ValidatesPidExecutableAndHashBeforeUsingAdminPort()
    {
        var metadata = await File.ReadAllTextAsync(GetTrayProjectPath("HostAgentRuntimeMetadata.cs"));
        var reader = await File.ReadAllTextAsync(GetTrayProjectPath("HostAgentRuntimeMetadataReader.cs"));

        AssertEx.Contains(metadata, "Pid");
        AssertEx.Contains(metadata, "ExePath");
        AssertEx.Contains(metadata, "ExeSha256");
        AssertEx.Contains(reader, "Process.GetProcessById");
        AssertEx.Contains(reader, "ProcessPathMatches");
        AssertEx.Contains(reader, "ComputeSha256");
        AssertEx.Contains(reader, "DeleteStaleMetadata");
    }

    [Test]
    public async Task TrayEntrypoint_WhenF4Implemented_UsesSingleInstanceAndLogModeArgument()
    {
        var program = await File.ReadAllTextAsync(GetTrayProjectPath("Program.cs"));
        var singleInstance = await File.ReadAllTextAsync(GetTrayProjectPath("TraySingleInstanceLock.cs"));

        AssertEx.Contains(program, "--log");
        AssertEx.Contains(program, "TraySingleInstanceLock.TryAcquire");
        AssertEx.Contains(program, "return 0;");
        AssertEx.Contains(singleInstance, "Local\\");
        AssertEx.Contains(singleInstance, "XDG_RUNTIME_DIR");
        AssertEx.Contains(singleInstance, "FileShare.None");
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
