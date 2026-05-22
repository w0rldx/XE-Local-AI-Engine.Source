namespace XE_Local_AI_Engine.Tests.HostAgent;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.HostAgent.Linux.Security;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel("HostAgentEnvironment")]
public sealed class HostAgentHmacOptionsTests
{
    private const string XdgRuntimeDir = "XDG_RUNTIME_DIR";
    private const string SecretFileEnv = "XE_HOST_AGENT_HMAC_SECRET_FILE";
    private const string RuntimeModeEnv = "XE_HOST_AGENT_RUNTIME_MODE";

    [Test]
    public void Bind_WhenSecretInConfig_UsesConfigSecret()
    {
        var original = CaptureEnvironment();
        try
        {
            Environment.SetEnvironmentVariable(SecretFileEnv, null);

            var configuration = BuildConfiguration(("HostAgent:Hmac:Secret", "config-secret"));
            var options = new HostAgentHmacOptions();

            HostAgentHmacOptions.Bind(options, configuration);

            AssertEx.Equal("config-secret", options.Secret);
        }
        finally
        {
            RestoreEnvironment(original);
        }
    }

    [Test]
    public void Bind_WhenSecretBlank_ReadsAndTrimsSecretFile()
    {
        var original = CaptureEnvironment();
        var tempDirectory = CreateTempDirectory();
        try
        {
            var secretFile = Path.Combine(tempDirectory, "hmac-secret");
            File.WriteAllText(secretFile, "  file-secret-value\n");
            Environment.SetEnvironmentVariable(SecretFileEnv, secretFile);

            var configuration = BuildConfiguration();
            var options = new HostAgentHmacOptions();

            HostAgentHmacOptions.Bind(options, configuration);

            AssertEx.Equal("file-secret-value", options.Secret);
        }
        finally
        {
            RestoreEnvironment(original);
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Test]
    public void ResolveSecretFilePath_PrefersConfigOverEnvOverDefault()
    {
        var original = CaptureEnvironment();
        try
        {
            // Case 1: config wins over env and default.
            Environment.SetEnvironmentVariable(SecretFileEnv, "/env/path/hmac-secret");
            var configWithSecretFile = BuildConfiguration(("HostAgent:Hmac:SecretFile", "/config/path/hmac-secret"));
            AssertEx.Equal("/config/path/hmac-secret", HostAgentHmacOptions.ResolveSecretFilePath(configWithSecretFile));

            // Case 2: env wins over default when config is absent.
            var emptyConfig = BuildConfiguration();
            AssertEx.Equal("/env/path/hmac-secret", HostAgentHmacOptions.ResolveSecretFilePath(emptyConfig));

            // Case 3: default used when neither config nor env present. The default branch depends
            // on whether the host is treated as a managed WSL runtime (/etc) or a native runtime (XDG),
            // so derive the expected value from IsManagedWslRuntime to stay host-independent.
            Environment.SetEnvironmentVariable(SecretFileEnv, null);
            Environment.SetEnvironmentVariable(RuntimeModeEnv, null);
            Environment.SetEnvironmentVariable(XdgRuntimeDir, "/run/user/1000");

            var expectedDefault = HostAgentHmacOptions.IsManagedWslRuntime()
                ? "/etc/xe-host-agent/hmac-secret"
                : Path.Combine("/run/user/1000", "xe-host-agent", "hmac-secret");

            var defaultPath = HostAgentHmacOptions.ResolveSecretFilePath(BuildConfiguration());
            AssertEx.Equal(expectedDefault, defaultPath);
        }
        finally
        {
            RestoreEnvironment(original);
        }
    }

    [Test]
    public void UsesNativeRuntimeSecretPath_TrueOnlyWhenUnderXdgRuntimeXeHostAgent()
    {
        var original = CaptureEnvironment();
        try
        {
            Environment.SetEnvironmentVariable(RuntimeModeEnv, null);
            Environment.SetEnvironmentVariable(XdgRuntimeDir, "/run/user/1000");

            // A path under the XDG runtime xe-host-agent directory => native.
            Environment.SetEnvironmentVariable(SecretFileEnv, "/run/user/1000/xe-host-agent/hmac-secret");
            AssertEx.True(HostAgentHmacOptions.UsesNativeRuntimeSecretPath(BuildConfiguration()));

            // A path outside the XDG runtime xe-host-agent directory => not native.
            Environment.SetEnvironmentVariable(SecretFileEnv, "/etc/xe-host-agent/hmac-secret");
            AssertEx.False(HostAgentHmacOptions.UsesNativeRuntimeSecretPath(BuildConfiguration()));

            // No XDG runtime directory at all => not native.
            Environment.SetEnvironmentVariable(SecretFileEnv, null);
            Environment.SetEnvironmentVariable(XdgRuntimeDir, null);
            AssertEx.False(HostAgentHmacOptions.UsesNativeRuntimeSecretPath(BuildConfiguration()));
        }
        finally
        {
            RestoreEnvironment(original);
        }
    }

    [Test]
    public void IsManagedWslRuntime_WhenRuntimeModeWslManaged_ReturnsTrue()
    {
        var original = CaptureEnvironment();
        try
        {
            Environment.SetEnvironmentVariable(RuntimeModeEnv, "wsl-managed");

            AssertEx.True(HostAgentHmacOptions.IsManagedWslRuntime());
        }
        finally
        {
            RestoreEnvironment(original);
        }
    }

    [Test]
    public void ResolveSecretFilePath_WhenManagedWsl_UsesEtcDefault()
    {
        var original = CaptureEnvironment();
        try
        {
            Environment.SetEnvironmentVariable(SecretFileEnv, null);
            Environment.SetEnvironmentVariable(XdgRuntimeDir, "/run/user/1000");
            Environment.SetEnvironmentVariable(RuntimeModeEnv, "wsl-managed");

            // Managed WSL default ignores XDG runtime in favour of the /etc path.
            AssertEx.Equal("/etc/xe-host-agent/hmac-secret", HostAgentHmacOptions.ResolveSecretFilePath(BuildConfiguration()));

            // Without the explicit wsl-managed flag the default depends on the host: a managed WSL
            // host still resolves to /etc, while a native host uses the XDG runtime path.
            Environment.SetEnvironmentVariable(RuntimeModeEnv, null);
            var expectedNativeOrWsl = HostAgentHmacOptions.IsManagedWslRuntime()
                ? "/etc/xe-host-agent/hmac-secret"
                : Path.Combine("/run/user/1000", "xe-host-agent", "hmac-secret");
            AssertEx.Equal(expectedNativeOrWsl, HostAgentHmacOptions.ResolveSecretFilePath(BuildConfiguration()));
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

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xe-hmac-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
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
