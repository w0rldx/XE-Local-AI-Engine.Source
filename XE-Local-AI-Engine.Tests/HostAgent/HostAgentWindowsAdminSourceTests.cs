namespace XE_Local_AI_Engine.Tests.HostAgent;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentWindowsAdminSourceTests
{
    private static readonly string ProjectRoot = GetProjectRoot();

    [Test]
    public async Task AdminHttp_WhenD5Implemented_IsLoopbackOnlyAndTokenProtected()
    {
        var program = await File.ReadAllTextAsync(GetWindowsProjectPath("Program.cs"));
        var endpoints = await File.ReadAllTextAsync(GetWindowsProjectPath("HostAgentAdminEndpoints.cs"));

        AssertEx.Contains(program, "IPAddress.Loopback");
        AssertEx.Contains(program, "UseLocalAdminRequestGuards");
        AssertEx.Contains(program, "MapLocalAdminEndpoints");
        AssertEx.Contains(endpoints, "MapGet(\"/status\"");
        AssertEx.Contains(endpoints, "MapGet(\"/logs\"");
        AssertEx.Contains(endpoints, "MapPost(\"/shutdown\"");
        AssertEx.Contains(endpoints, "MapPost(\"/startup\"");
        AssertEx.Contains(endpoints, "MapPost(\"/restart\"");
        AssertEx.Contains(endpoints, "Results.Unauthorized()");
        AssertEx.Contains(endpoints, "Request.Headers.Authorization");
        AssertEx.Contains(endpoints, "CryptographicOperations.FixedTimeEquals");
        AssertEx.Contains(endpoints, "127.0.0.1");
        AssertEx.Contains(endpoints, "Headers.Origin");
        AssertEx.False(endpoints.Contains("UseCors", StringComparison.Ordinal));
    }

    [Test]
    public async Task DesiredState_WhenD5Implemented_PersistsStoppedAndSuppressesSupervisorColdStart()
    {
        var paths = await File.ReadAllTextAsync(GetWindowsProjectPath("HostAgentWindowsPaths.cs"));
        var store = await File.ReadAllTextAsync(GetWindowsProjectPath("DesiredStateStore.cs"));
        var supervisor = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "WslSupervisorHostedService.cs"));

        AssertEx.Contains(paths, "desired-state.json");
        AssertEx.Contains(store, "public const string Running = \"running\"");
        AssertEx.Contains(store, "public const string Stopped = \"stopped\"");
        AssertEx.Contains(store, "SetDesiredStateAsync");
        AssertEx.Contains(supervisor, "DesiredStateStore");
        AssertEx.Contains(supervisor, "DesiredStateStore.Stopped");
        AssertEx.Contains(supervisor, "return;");
    }

    [Test]
    public async Task LifecycleEndpoints_WhenD5Implemented_UseD4ClientAndWslShutdownChoreography()
    {
        var service = await File.ReadAllTextAsync(GetWindowsProjectPath("HostAgentAdminService.cs"));
        var driver = await File.ReadAllTextAsync(GetWindowsProjectPath("Wsl", "Wsl2Driver.cs"));

        AssertEx.Contains(service, "StopAllContainersAsync(DefaultDrainTimeout");
        AssertEx.Contains(service, "StopUserUnitAsync");
        AssertEx.Contains(service, "TerminateAsync");
        AssertEx.Contains(service, "ColdStartAsync");
        AssertEx.Contains(service, "StartAllContainersAsync");
        AssertEx.Contains(service, "SemaphoreSlim _lifecycleLock");
        AssertEx.Contains(driver, "StopUserUnitAsync");
        AssertEx.Contains(driver, "UserSystemctl(_options.DistroName, \"stop\")");
    }

    [Test]
    public async Task AdminHttp_WhenD5Implemented_DoesNotLogAdminTokenMaterial()
    {
        var endpoints = await File.ReadAllTextAsync(GetWindowsProjectPath("HostAgentAdminEndpoints.cs"));
        var service = await File.ReadAllTextAsync(GetWindowsProjectPath("HostAgentAdminService.cs"));

        AssertEx.False(endpoints.Contains("ILogger", StringComparison.Ordinal));
        AssertEx.False(endpoints.Contains("LogInformation", StringComparison.Ordinal));
        AssertEx.False(endpoints.Contains("LogWarning", StringComparison.Ordinal));
        AssertEx.False(service.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(service.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetWindowsProjectPath(params string[] relativePath)
    {
        return Path.Combine([
            ProjectRoot,
            "Apps",
            "XE-Local-AI-Engine",
            "XE-Local-AI-Engine.HostAgent.Windows",
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
