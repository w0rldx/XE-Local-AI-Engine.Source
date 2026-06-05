namespace XE_Local_AI_Engine.Tests.HostAgent;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentSystemdUnitTests
{
    private static readonly string ProjectRoot = GetProjectRoot();

    [Test]
    public async Task UserUnit_DeclaresForegroundHostAgentProcess()
    {
        var unit = await File.ReadAllTextAsync(GetPackagingPath("systemd/xe-host-agent.service"));

        AssertEx.Contains(unit, "[Service]");
        AssertEx.Contains(unit, "Type=simple");
        AssertEx.Contains(unit, "ExecStart=%h/.local/share/xe-host-agent/bin/XE-Local-AI-Engine.HostAgent.Linux");
        AssertEx.Contains(unit, "WantedBy=default.target");
    }

    [Test]
    public async Task InstallerScript_DropsUnitWithoutEnableStartOrNativeLinger()
    {
        var script = await File.ReadAllTextAsync(GetPackagingPath("install-user-unit.sh"));

        AssertEx.Contains(script, "install -m 0644");
        AssertEx.Contains(script, "systemctl --user daemon-reload");
        AssertEx.False(ContainsExecutableLine(script, "systemctl --user enable"));
        AssertEx.False(ContainsExecutableLine(script, "systemctl --user start"));
        AssertEx.False(ContainsExecutableLine(script, "loginctl enable-linger"));
    }

    [Test]
    public async Task InstallerScript_WhenF4Implemented_DropsApplicationLaunchersWithoutAutostart()
    {
        var script = await File.ReadAllTextAsync(GetPackagingPath("install-user-unit.sh"));

        AssertEx.Contains(script, "/usr/share/applications");
        AssertEx.Contains(script, "xe-local-ai-engine.desktop");
        AssertEx.Contains(script, "xe-local-ai-engine-log.desktop");
        AssertEx.Contains(script, "Exec=${TRAY_EXECUTABLE}");
        AssertEx.Contains(script, "Exec=${TRAY_EXECUTABLE} --log");
        AssertEx.Contains(script, "/usr/share/icons/hicolor/256x256/apps");
        AssertEx.Contains(script, "Icon=${ICON_TARGET}");
        AssertEx.False(script.Contains("/autostart", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task HostAgentLinux_WhenStartedAsRoot_RefusesRuntimeIdentity()
    {
        var program = await File.ReadAllTextAsync(GetLinuxProjectPath("Program.cs"));

        AssertEx.Contains(program, "LINUX_REFUSES_ROOT_RUNTIME");
        AssertEx.Contains(program, "GetEffectiveUserId() == 0");
    }

    [Test]
    public async Task HmacOptions_WhenNativeLinux_UsesXdgRuntimeSecretByDefault()
    {
        var options = await File.ReadAllTextAsync(GetLinuxProjectPath("Security", "HostAgentHmacOptions.cs"));
        var bootstrap = await File.ReadAllTextAsync(GetLinuxProjectPath("Security", "HostAgentHmacSecretBootstrap.cs"));

        AssertEx.Contains(options, "XDG_RUNTIME_DIR");
        AssertEx.Contains(options, "Path.Combine(xdgRuntimeDirectory, \"xe-host-agent\", \"hmac-secret\")");
        AssertEx.Contains(options, "IsManagedWslRuntime");
        AssertEx.Contains(bootstrap, "EnsureNativeSecret");

        // Behavioural coverage for the random secret generation and owner-only file mode now lives in
        // HostAgentHmacSecretBootstrapTests; the brittle source-text assertions were removed.
    }

    [Test]
    public async Task HostAgentLinux_WhenDockerSocketIsMissing_RunsRootlessDockerSetupOnFirstStart()
    {
        var program = await File.ReadAllTextAsync(GetLinuxProjectPath("Program.cs"));
        var bootstrap = await File.ReadAllTextAsync(GetLinuxProjectPath("Docker", "Implementation", "RootlessDockerBootstrapHostedService.cs"));

        AssertEx.Contains(program, "AddHostedService<RootlessDockerBootstrapHostedService>");
        AssertEx.Contains(bootstrap, "dockerd-rootless-setuptool.sh");
        AssertEx.Contains(bootstrap, "install");
        AssertEx.Contains(bootstrap, "DockerSocketExists");
    }

    [Test]
    public async Task RuntimeMetadata_WhenAdminPortIsDynamic_DoesNotPublishGrpcTcpPort()
    {
        var metadata = await File.ReadAllTextAsync(GetLinuxProjectPath("Services", "HostAgentRuntimeMetadataHostedService.cs"));

        AssertEx.Contains(metadata, "HostAgentAdminOptions");
        AssertEx.Contains(metadata, "HostAgentTcpOptions");
        AssertEx.Contains(metadata, "_adminOptions.Port > 0");
        AssertEx.Contains(metadata, "uri!.Port != _tcpOptions.Port");
    }

    [Test]
    public async Task HostAgentGrpcContract_DoesNotExposeModelManagementMethods()
    {
        var proto = await File.ReadAllTextAsync(GetLinuxProjectPath("..", "XE-Local-AI-Engine.HostAgent.Grpc.Contracts", "Protos", "host_agent.proto"));

        AssertEx.False(proto.Contains("rpc PullModel", StringComparison.Ordinal));
        AssertEx.False(proto.Contains("PullModelRequest", StringComparison.Ordinal));
    }

    private static string GetPackagingPath(string relativePath)
    {
        return Path.Combine(ProjectRoot,
            "Apps",
            "XE-Local-AI-Engine",
            "XE-Local-AI-Engine.HostAgent.Linux",
            "Packaging",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string GetLinuxProjectPath(params string[] relativePath)
    {
        return Path.Combine([
            ProjectRoot,
            "Apps",
            "XE-Local-AI-Engine",
            "XE-Local-AI-Engine.HostAgent.Linux",
            .. relativePath
        ]);
    }

    private static bool ContainsExecutableLine(string script, string commandPrefix)
    {
        return script.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                     .Any(line => line.StartsWith(commandPrefix, StringComparison.Ordinal));
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
