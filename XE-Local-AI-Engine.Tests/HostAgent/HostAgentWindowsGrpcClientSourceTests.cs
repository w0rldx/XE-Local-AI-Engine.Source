namespace XE_Local_AI_Engine.Tests.HostAgent;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentWindowsGrpcClientSourceTests
{
    private static readonly string ProjectRoot = GetProjectRoot();

    [Test]
    public async Task WindowsHostAgent_WhenD4Implemented_WiresGrpcClientWithHmacAndRetryBackoff()
    {
        var project = await File.ReadAllTextAsync(GetWindowsProjectPath("XE-Local-AI-Engine.HostAgent.Windows.csproj"));
        var program = await File.ReadAllTextAsync(GetWindowsProjectPath("Program.cs"));
        var client = await File.ReadAllTextAsync(GetWindowsProjectPath("Implementation", "HostAgentLinuxGrpcClient.cs"));
        var options = await File.ReadAllTextAsync(GetWindowsProjectPath("HostAgentLinuxGrpcOptions.cs"));

        AssertEx.Contains(project, "Grpc.Net.Client");
        AssertEx.Contains(project, "XE-Local-AI-Engine.HostAgent.Grpc.Contracts.csproj");
        AssertEx.Contains(program, "HostAgentLinuxGrpcOptions.Bind");
        AssertEx.Contains(program, "IHostAgentLinuxClient, HostAgentLinuxGrpcClient");
        AssertEx.Contains(options, "http://127.0.0.1:57974");
        AssertEx.Contains(client, "HostAgentHmacMetadata.Create");
        AssertEx.Contains(client, "ServiceConfig");
        AssertEx.Contains(client, "RetryPolicy");
        AssertEx.Contains(client, "StatusCode.Unavailable");
    }

    // Lane D deleted the HostAgent.Linux daemon project; the Linux-side loopback-TCP source assertion that read its
    // Program.cs / HostAgentTcpOptions.cs is dropped with the daemon it covered. The Windows gRPC client wiring above
    // (the kept connection layer) stays under test.

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
