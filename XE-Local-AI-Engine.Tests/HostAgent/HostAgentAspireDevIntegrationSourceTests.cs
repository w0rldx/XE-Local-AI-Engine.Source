namespace XE_Local_AI_Engine.Tests.HostAgent;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentAspireDevIntegrationSourceTests
{
    private static readonly string ProjectRoot = GetProjectRoot();

    [Test]
    public async Task AppHost_WhenG1Implemented_GatesHostAgentLinuxDevRigOnEnvironmentFlag()
    {
        var appHost = await File.ReadAllTextAsync(GetXePath("XE-Local-AI-Engine.AppHost", "AppHost.cs"));
        var project = await File.ReadAllTextAsync(GetXePath("XE-Local-AI-Engine.AppHost", "XE-Local-AI-Engine.AppHost.csproj"));

        AssertEx.Contains(appHost, "XE_ENABLE_HOST_AGENT_DEV");
        AssertEx.Contains(appHost, "xe-host-agent-linux");
        AssertEx.Contains(appHost, "XE_Local_AI_Engine_HostAgent_Linux");
        AssertEx.Contains(appHost, "HostAgent__Docker__UseFakeDriver");
        AssertEx.Contains(appHost, "HostAgent__Hmac__Secret");
        AssertEx.Contains(appHost, "HostAgent__Client__Secret");
        AssertEx.Contains(appHost, "HostAgent__Client__SocketPath");
        AssertEx.Contains(appHost, "WaitFor(hostAgentLinux)");
        AssertEx.Contains(project, "XE-Local-AI-Engine.HostAgent.Linux.csproj");
    }

    [Test]
    public async Task HostAgentLinux_WhenG1Implemented_CanUseFakeDockerDriverAndAspireConnectionStringEndpoint()
    {
        var program = await File.ReadAllTextAsync(GetXePath("XE-Local-AI-Engine.HostAgent.Linux", "Program.cs"));
        var options = await File.ReadAllTextAsync(GetXePath("XE-Local-AI-Engine.HostAgent.Linux", "Docker", "HostAgentDockerOptions.cs"));
        var fakeDriver = await File.ReadAllTextAsync(GetXePath("XE-Local-AI-Engine.HostAgent.Linux", "Docker", "FakeDockerRuntimeClient.cs"));

        AssertEx.Contains(options, "UseFakeDriver");
        AssertEx.Contains(program, "UseFakeDriver");
        AssertEx.Contains(program, "FakeDockerRuntimeClient");
        AssertEx.Contains(program, "GetConnectionString(\"chat\")");
        AssertEx.Contains(fakeDriver, "IDockerRuntimeClient");
        AssertEx.Contains(fakeDriver, "xe-node-web-server");
        AssertEx.Contains(fakeDriver, "ollama");
    }

    private static string GetXePath(params string[] relativePath)
    {
        return Path.Combine([
            ProjectRoot,
            "Apps",
            "XE-Local-AI-Engine",
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
