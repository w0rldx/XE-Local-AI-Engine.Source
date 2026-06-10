namespace XE_Local_AI_Engine.Installer.Driver.Windows;

using System.Globalization;

/// <summary>
///     Resolves the fixed locations inside an unzipped RC bundle (plan §5) and translates a Windows
///     path to its WSL <c>/mnt/&lt;drive&gt;/…</c> form. Pure functions so they are unit-testable
///     cross-platform.
/// </summary>
public static class BundleLayout
{
    public static string MetadataPath(string bundlePath) =>
        Path.Combine(bundlePath, "payload", "bundle-metadata.json");

    public static string RootfsTarPath(string bundlePath) =>
        Path.Combine(bundlePath, "payload", "rootfs", "ubuntu.tar.gz");

    public static string ImageTarPath(string bundlePath) =>
        Path.Combine(bundlePath, "payload", "images", "xe-node-web-server.tar.gz");

    public static string InDistroScriptPath(string bundlePath, string scriptFileName) =>
        Path.Combine(bundlePath, "payload", "in-distro-scripts", scriptFileName);

    public static string HostAgentSourceDir(string bundlePath) =>
        Path.Combine(bundlePath, "payload", "host-agent");

    public static string VendoredScriptPath(string bundlePath, string scriptFileName) =>
        Path.Combine(bundlePath, "payload", "scripts", scriptFileName);

    public static string ManifestPath(string bundlePath) =>
        Path.Combine(bundlePath, "payload", "manifest", "managed.yaml");

    /// <summary>
    ///     Translate a Windows path (e.g. <c>C:\rc\payload\images\img.tar.gz</c>) to the WSL auto-mount
    ///     form <c>/mnt/c/rc/payload/images/img.tar.gz</c>. WSL2 mounts each fixed drive at
    ///     <c>/mnt/&lt;lowercase-letter&gt;</c>; backslashes become forward slashes. A path that is
    ///     already POSIX-absolute (no drive letter) is returned unchanged — that is the namespace a
    ///     future Linux driver would hand straight to the distro, and it keeps this logic testable on a
    ///     non-Windows CI host. A relative path is rejected (it has no unambiguous in-distro location).
    /// </summary>
    public static string ToWslMountPath(string windowsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsPath);

        var hasDriveLetter = windowsPath.Length >= 2
                             && char.IsLetter(windowsPath[0])
                             && windowsPath[1] == ':';
        if (hasDriveLetter)
        {
            var driveLetter = char.ToLower(windowsPath[0], CultureInfo.InvariantCulture);
            var remainder = windowsPath[2..].Replace('\\', '/').TrimStart('/');
            return $"/mnt/{driveLetter}/{remainder}";
        }

        if (windowsPath.StartsWith('/'))
        {
            return windowsPath;
        }

        throw new InvalidOperationException(
            $"Cannot translate '{windowsPath}' to a WSL mount path: expected a drive-letter path like C:\\... or a POSIX-absolute path.");
    }
}
