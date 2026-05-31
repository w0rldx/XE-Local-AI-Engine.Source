namespace XE_Local_AI_Engine.HostAgent.Linux.Security;

using System.Security.Cryptography;

/// <summary>
///     Represents host agent hmac secret bootstrap.
/// </summary>
public static class HostAgentHmacSecretBootstrap
{
    public static void EnsureNativeSecret(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.IsNullOrWhiteSpace(configuration["HostAgent:Hmac:Secret"]))
        {
            return;
        }

        var path = HostAgentHmacOptions.ResolveSecretFilePath(configuration);
        if (File.Exists(path) || !HostAgentHmacOptions.UsesNativeRuntimeSecretPath(configuration))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
