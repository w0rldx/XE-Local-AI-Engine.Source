namespace XE_Local_AI_Engine.Tests.HostAgent;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.HostAgent.Linux.Security;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel("HostAgentEnvironment")]
public sealed class HostAgentHmacSecretBootstrapTests
{
    private const string XdgRuntimeDir = "XDG_RUNTIME_DIR";
    private const string SecretFileEnv = "XE_HOST_AGENT_HMAC_SECRET_FILE";
    private const string RuntimeModeEnv = "XE_HOST_AGENT_RUNTIME_MODE";

    [Test]
    public void EnsureNativeSecret_WhenSecretConfigured_DoesNotWriteFile()
    {
        var original = CaptureEnvironment();
        var tempDirectory = CreateTempRuntimeDirectory();
        try
        {
            Environment.SetEnvironmentVariable(XdgRuntimeDir, tempDirectory);
            Environment.SetEnvironmentVariable(SecretFileEnv, null);
            Environment.SetEnvironmentVariable(RuntimeModeEnv, null);

            var configuration = BuildConfiguration(("HostAgent:Hmac:Secret", "configured-secret"));

            HostAgentHmacSecretBootstrap.EnsureNativeSecret(configuration);

            var secretPath = Path.Combine(tempDirectory, "xe-host-agent", "hmac-secret");
            AssertEx.False(File.Exists(secretPath), "Expected no secret file when a secret is configured.");
        }
        finally
        {
            RestoreEnvironment(original);
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Test]
    public void EnsureNativeSecret_WhenNativeRuntimePathAndFileMissing_Writes64HexCharSecret()
    {
        var original = CaptureEnvironment();
        var tempDirectory = CreateTempRuntimeDirectory();
        try
        {
            Environment.SetEnvironmentVariable(XdgRuntimeDir, tempDirectory);
            Environment.SetEnvironmentVariable(RuntimeModeEnv, null);

            // Pin the secret file under the XDG runtime xe-host-agent directory so the path is
            // recognised as native regardless of host osrelease (the default-path resolution treats
            // WSL hosts as /etc-managed, which would otherwise suppress the write).
            var secretPath = Path.Combine(tempDirectory, "xe-host-agent", "hmac-secret");
            Environment.SetEnvironmentVariable(SecretFileEnv, secretPath);

            var configuration = BuildConfiguration();

            HostAgentHmacSecretBootstrap.EnsureNativeSecret(configuration);

            AssertEx.True(File.Exists(secretPath), "Expected secret file to be written under the native runtime path.");

            var contents = File.ReadAllText(secretPath);
            AssertEx.Equal(64, contents.Length);

            var decoded = Convert.FromHexString(contents);
            AssertEx.Equal(32, decoded.Length);
        }
        finally
        {
            RestoreEnvironment(original);
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Test]
    public void EnsureNativeSecret_WhenFileAlreadyExists_DoesNotOverwrite()
    {
        var original = CaptureEnvironment();
        var tempDirectory = CreateTempRuntimeDirectory();
        try
        {
            Environment.SetEnvironmentVariable(XdgRuntimeDir, tempDirectory);
            Environment.SetEnvironmentVariable(SecretFileEnv, null);
            Environment.SetEnvironmentVariable(RuntimeModeEnv, null);

            var secretPath = Path.Combine(tempDirectory, "xe-host-agent", "hmac-secret");
            Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);
            const string sentinel = "existing-secret-sentinel";
            File.WriteAllText(secretPath, sentinel);

            var configuration = BuildConfiguration();

            HostAgentHmacSecretBootstrap.EnsureNativeSecret(configuration);

            AssertEx.Equal(sentinel, File.ReadAllText(secretPath));
        }
        finally
        {
            RestoreEnvironment(original);
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Test]
    public void EnsureNativeSecret_WhenNotNativeRuntimePath_DoesNotWrite()
    {
        var original = CaptureEnvironment();
        var tempDirectory = CreateTempRuntimeDirectory();
        try
        {
            Environment.SetEnvironmentVariable(XdgRuntimeDir, tempDirectory);
            Environment.SetEnvironmentVariable(RuntimeModeEnv, null);

            // Point the secret file outside the native XDG runtime path (default /etc style).
            var nonNativePath = Path.Combine(tempDirectory, "etc", "xe-host-agent", "hmac-secret");
            Environment.SetEnvironmentVariable(SecretFileEnv, nonNativePath);

            var configuration = BuildConfiguration();

            HostAgentHmacSecretBootstrap.EnsureNativeSecret(configuration);

            AssertEx.False(File.Exists(nonNativePath), "Expected no secret to be written for a non-native runtime path.");
        }
        finally
        {
            RestoreEnvironment(original);
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Test]
    public void EnsureNativeSecret_SetsOwnerOnlyFileMode()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var original = CaptureEnvironment();
        var tempDirectory = CreateTempRuntimeDirectory();
        try
        {
            Environment.SetEnvironmentVariable(XdgRuntimeDir, tempDirectory);
            Environment.SetEnvironmentVariable(RuntimeModeEnv, null);

            var secretPath = Path.Combine(tempDirectory, "xe-host-agent", "hmac-secret");
            Environment.SetEnvironmentVariable(SecretFileEnv, secretPath);

            var configuration = BuildConfiguration();

            HostAgentHmacSecretBootstrap.EnsureNativeSecret(configuration);

            AssertEx.True(File.Exists(secretPath), "Expected secret file to be written.");

            var mode = File.GetUnixFileMode(secretPath);
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            RestoreEnvironment(original);
            DeleteTempDirectory(tempDirectory);
        }
    }

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
               .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value))
               .Build();
    }

    private static string CreateTempRuntimeDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xe-hmac-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static (string? Xdg, string? SecretFile, string? RuntimeMode) CaptureEnvironment()
    {
        return (
            Environment.GetEnvironmentVariable(XdgRuntimeDir),
            Environment.GetEnvironmentVariable(SecretFileEnv),
            Environment.GetEnvironmentVariable(RuntimeModeEnv));
    }

    private static void RestoreEnvironment((string? Xdg, string? SecretFile, string? RuntimeMode) original)
    {
        Environment.SetEnvironmentVariable(XdgRuntimeDir, original.Xdg);
        Environment.SetEnvironmentVariable(SecretFileEnv, original.SecretFile);
        Environment.SetEnvironmentVariable(RuntimeModeEnv, original.RuntimeMode);
    }
}
