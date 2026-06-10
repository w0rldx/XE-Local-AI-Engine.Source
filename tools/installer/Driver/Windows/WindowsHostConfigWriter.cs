namespace XE_Local_AI_Engine.Installer.Driver.Windows;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

/// <summary>
///     Production <see cref="IInstallerHostConfigWriter" />. Lays out the Windows host-agent ProgramData
///     directory (mirrors <c>HostAgentWindowsPaths.CreateDefault()</c>): ensures <c>logs/</c> and
///     <c>secrets/</c> exist and writes a freshly generated, DPAPI-protected admin token to
///     <c>secrets/admin-token.dpapi</c>. The token is created on the tester's machine and never shipped
///     (plan §10).
///
///     It deliberately does NOT write <c>runtime.json</c>: that path is
///     <c>HostAgentWindowsPaths.RuntimeMetadataPath</c>, owned by the Windows HostAgent's
///     <c>RuntimeMetadataHostedService</c>, which overwrites it with process metadata JSON on start and
///     deletes it on stop (HIGH-1). It also does NOT deliver the runtime manifest — the in-distro Linux
///     host agent reads its manifest from <c>$XDG_CONFIG_HOME/xe-host-agent/manifest.yaml</c> inside the
///     distro (bound into <c>HostAgent:Runtime</c> config), NOT from any Windows-side file; that delivery
///     is the driver's in-distro step (<c>WindowsInstallerDriver.WriteConfigAsync</c>), not host-FS work.
/// </summary>
public sealed class WindowsHostConfigWriter : IInstallerHostConfigWriter
{
    public async Task WriteAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Path.GetTempPath();
        }

        var rootDirectory = Path.Combine(programData, "XE-Local-AI-Engine", "host-agent");
        var secretsDirectory = Path.Combine(rootDirectory, "secrets");
        Directory.CreateDirectory(Path.Combine(rootDirectory, "logs"));
        Directory.CreateDirectory(secretsDirectory);
        HardenSecretsDirectory(secretsDirectory);

        var tokenPath = Path.Combine(secretsDirectory, "admin-token.dpapi");
        var token = RandomNumberGenerator.GetBytes(32);
        var protectedToken = ProtectToken(token);
        await File.WriteAllBytesAsync(tokenPath, protectedToken, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] ProtectToken(byte[] token)
    {
        if (!OperatingSystem.IsWindows())
        {
            // RC1 ships Windows-only; the orchestrator only instantiates this writer under Windows.
            throw new PlatformNotSupportedException("The DPAPI admin-token protection is available on Windows only.");
        }

        return ProtectWithDpapi(token);
    }

    // sec LOW-1b — DPAPI scope decision: LocalMachine (not CurrentUser). The token is written by the
    // elevated installer and must be readable by the HostAgent process regardless of which user account
    // runs it on this single-tester machine; CurrentUser scope would bind it to the installing identity
    // and break a differently-scoped HostAgent launch. Defence-in-depth comes from the secrets/ directory
    // ACL (SYSTEM + Administrators only) applied below, so a non-admin local user cannot read the file to
    // attempt machine-scoped unprotect. A per-install entropy/user-scope hardening is an RC2 item.
    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWithDpapi(byte[] token) =>
        ProtectedData.Protect(token, optionalEntropy: null, DataProtectionScope.LocalMachine);

    private static void HardenSecretsDirectory(string secretsDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RestrictToSystemAndAdministrators(secretsDirectory);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToSystemAndAdministrators(string secretsDirectory)
    {
        var directoryInfo = new DirectoryInfo(secretsDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);

        foreach (var account in new[] { system, administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        directoryInfo.SetAccessControl(security);
    }
}
