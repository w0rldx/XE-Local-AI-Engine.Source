namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Text;
using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.HostAgent.Linux.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel("HostAgentEnvironment")]
public sealed class HostAgentSocketPathTests
{
    private const string XdgRuntimeDir = "XDG_RUNTIME_DIR";
    private const string Uid = "UID";
    private const string SocketEnv = "XE_HOST_AGENT_SOCKET";

    [Test]
    public void GetDefaultSocketPath_StaysUnderSunPathLimit()
    {
        var original = CaptureEnvironment();
        try
        {
            // A realistic long XDG_RUNTIME_DIR (systemd-style runtime path for a high UID).
            Environment.SetEnvironmentVariable(XdgRuntimeDir, "/run/user/4294967294");

            var path = HostAgentSocketPaths.GetDefaultSocketPath();

            var byteCount = Encoding.UTF8.GetByteCount(path);
            AssertEx.True(
                byteCount < HostAgentSocketOptions.SunPathMaxBytes,
                $"Default socket path '{path}' is {byteCount} bytes; expected < {HostAgentSocketOptions.SunPathMaxBytes}.");
        }
        finally
        {
            RestoreEnvironment(original);
        }
    }

    [Test]
    public async Task FromConfiguration_WhenConfiguredPathExceedsLimit_ThrowsClearError()
    {
        var original = CaptureEnvironment();
        try
        {
            Environment.SetEnvironmentVariable(SocketEnv, null);

            // 114-byte path mirrors the regression that crashed startup.
            var oversizedPath = "/run/user/1000/xe-host-agent/" + new string('a', 86);
            AssertEx.True(
                Encoding.UTF8.GetByteCount(oversizedPath) >= HostAgentSocketOptions.SunPathMaxBytes,
                "Test fixture path must exceed the sun_path limit.");

            var configuration = BuildConfiguration(("HostAgent:SocketPath", oversizedPath));

            var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(
                () => Task.FromResult(HostAgentSocketOptions.FromConfiguration(configuration)));

            AssertEx.Contains(exception.Message, oversizedPath);
            AssertEx.Contains(exception.Message, HostAgentSocketOptions.SunPathMaxBytes.ToString());
        }
        finally
        {
            RestoreEnvironment(original);
        }
    }

    [Test]
    public void FromConfiguration_HonorsConfiguredPath()
    {
        var original = CaptureEnvironment();
        try
        {
            Environment.SetEnvironmentVariable(SocketEnv, null);

            const string configuredPath = "/tmp/xe-host-agent/custom.sock";
            var configuration = BuildConfiguration(("HostAgent:SocketPath", configuredPath));

            var options = HostAgentSocketOptions.FromConfiguration(configuration);

            AssertEx.Equal(configuredPath, options.SocketPath);
        }
        finally
        {
            RestoreEnvironment(original);
        }
    }

    [Test]
    public void FromConfiguration_FallsBackToDefaultWhenBlank()
    {
        var original = CaptureEnvironment();
        try
        {
            Environment.SetEnvironmentVariable(SocketEnv, null);
            Environment.SetEnvironmentVariable(XdgRuntimeDir, "/run/user/1000");

            var configuration = BuildConfiguration(("HostAgent:SocketPath", "   "));

            var options = HostAgentSocketOptions.FromConfiguration(configuration);

            AssertEx.Equal(HostAgentSocketPaths.GetDefaultSocketPath(), options.SocketPath);
        }
        finally
        {
            RestoreEnvironment(original);
        }
    }

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();
    }

    private static (string? Xdg, string? Uid, string? Socket) CaptureEnvironment()
    {
        return (
            Environment.GetEnvironmentVariable(XdgRuntimeDir),
            Environment.GetEnvironmentVariable(Uid),
            Environment.GetEnvironmentVariable(SocketEnv));
    }

    private static void RestoreEnvironment((string? Xdg, string? Uid, string? Socket) original)
    {
        Environment.SetEnvironmentVariable(XdgRuntimeDir, original.Xdg);
        Environment.SetEnvironmentVariable(Uid, original.Uid);
        Environment.SetEnvironmentVariable(SocketEnv, original.Socket);
    }
}
