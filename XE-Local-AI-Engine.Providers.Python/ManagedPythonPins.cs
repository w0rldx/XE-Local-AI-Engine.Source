namespace XE_Local_AI_Engine.Providers.Python;

using System.Runtime.InteropServices;

/// <summary>The pinned uv release every uv-managed venv the engine provisions is built with, per runtime identifier.</summary>
/// <remarks>
///     Unlike llama.cpp, whose digests come from the GitHub release-assets API for want of sidecars, uv publishes a
///     <c>.sha256</c> per asset; re-fetch both on a version bump from
///     <c>https://github.com/astral-sh/uv/releases/download/&lt;tag&gt;/&lt;asset&gt;.sha256</c>. One version for every RID.
/// </remarks>
public static class ManagedPythonPins
{
    public const string UvVersion = "0.13.6";

    /// <summary>The one CPython minor every profile runs on; each lockfile's <c>requires-python</c> must admit exactly this minor.</summary>
    public const string PythonMinor = "3.14";

    public static readonly ManagedPythonUvAsset LinuxX64 = new()
    {
        Rid = "linux-x64",
        AssetName = "uv-x86_64-unknown-linux-gnu.tar.gz",
        Sha256 = "f29c3e94ffabd7e6f3ba521dba169a9b7ec12100bf3257087f0f24f6123f3645",
        ArchiveRootDirectory = "uv-x86_64-unknown-linux-gnu",
        ExecutableName = "uv",
        IsZip = false
    };

    // The Windows zip has no root directory: uv.exe, uvw.exe and uvx.exe sit at the top level.
    public static readonly ManagedPythonUvAsset WindowsX64 = new()
    {
        Rid = "win-x64",
        AssetName = "uv-x86_64-pc-windows-msvc.zip",
        Sha256 = "a2a8820d5eea32f08ef467ef3b7f66883a9b076d5737a2fc2fbb5f458d1bb50f",
        ArchiveRootDirectory = "",
        ExecutableName = "uv.exe",
        IsZip = true
    };

    /// <summary>The asset for this process's OS and architecture.</summary>
    /// <exception cref="ManagedPythonException">No uv asset is pinned for this platform.</exception>
    public static ManagedPythonUvAsset Current => Resolve(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture);

    /// <summary>True when <see cref="Current" /> resolves; lets a status read skip the store instead of throwing.</summary>
    public static bool IsCurrentPlatformSupported => ResolveOrNull(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture) is not null;

    internal static ManagedPythonUvAsset Resolve(bool isWindows, bool isLinux, Architecture architecture)
    {
        return ResolveOrNull(isWindows, isLinux, architecture)
               ?? throw new ManagedPythonException("Managed Python is available on Linux x64 and Windows x64 only.");
    }

    private static ManagedPythonUvAsset? ResolveOrNull(bool isWindows, bool isLinux, Architecture architecture)
    {
        return (isWindows, isLinux, architecture) switch
        {
            (true, _, Architecture.X64) => WindowsX64,
            (_, true, Architecture.X64) => LinuxX64,
            _ => null
        };
    }
}
