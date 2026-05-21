namespace XE_Local_AI_Engine.HostAgent.Windows;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

public sealed class DpapiCurrentUserSecretProtector : IHostAgentSecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("XE-Local-AI-Engine.HostAgent.Windows.SecretStore.v1");

    [SupportedOSPlatform("windows")]
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI current-user protection is only available on Windows.");
        }

        return ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }

    [SupportedOSPlatform("windows")]
    public byte[] Unprotect(byte[] protectedPayload)
    {
        ArgumentNullException.ThrowIfNull(protectedPayload);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI current-user protection is only available on Windows.");
        }

        return ProtectedData.Unprotect(protectedPayload, Entropy, DataProtectionScope.CurrentUser);
    }
}
