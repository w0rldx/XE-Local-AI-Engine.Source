namespace XE_Local_AI_Engine.Providers.Python.Implementation;

/// <summary>
///     Builds the scrubbed, allow-listed environment uv runs under: ONLY the keys named here pass through, and
///     everything else is dropped by construction.
/// </summary>
/// <remarks>
///     Both runners clear the inherited environment before applying the result. The training
///     runtime layers its probe, train and export environments on <see cref="BuildAllowlisted()" />. See
///     docs/wiki/18-training.md ("The scrubbed environments, and the uv pipeline the compute tool shares").
/// </remarks>
public static class ManagedPythonEnvironment
{
    private static readonly string[] UnixAllowlist = ["PATH", "LANG", "LC_ALL", "CUDA_HOME", "CUDA_PATH"];

    // Windows adds what a process needs to start at all: without SystemRoot, CPython's random-number and Winsock
    // initialisation fail, and windir/SystemDrive/PATHEXT are what Win32 path and executable lookup read.
    private static readonly string[] WindowsAllowlist = [.. UnixAllowlist, "SystemRoot", "SystemDrive", "windir", "PATHEXT"];

    /// <summary>The environment for uv itself.</summary>
    /// <remarks>
    ///     uv is pointed at isolated HOME/TMPDIR and cache/interpreter directories under the caller's cache root, so
    ///     the install neither reads the operator's <c>~/.config/uv</c> — which could redirect an index — nor scatters
    ///     gigabytes into the user's home.
    /// </remarks>
    public static Dictionary<string, string> BuildUvEnvironment(string isolatedHome, string isolatedTmp, string uvCacheDirectory, string pythonInstallDirectory)
    {
        return BuildUvEnvironment(isolatedHome, isolatedTmp, uvCacheDirectory, pythonInstallDirectory, OperatingSystem.IsWindows());
    }

    /// <summary>Test seam: the uv environment for an explicit OS, so the Windows branch is covered on any host.</summary>
    internal static Dictionary<string, string> BuildUvEnvironment(string isolatedHome,
        string isolatedTmp,
        string uvCacheDirectory,
        string pythonInstallDirectory,
        bool isWindows)
    {
        var scrubbed = BuildAllowlisted(isWindows);
        scrubbed["HOME"] = isolatedHome;
        scrubbed["TMPDIR"] = isolatedTmp;
        if (isWindows)
        {
            // Windows reads neither HOME nor TMPDIR for these: Rust's home_dir and Python's expanduser read USERPROFILE,
            // and GetTempPath2 (uv) and Python's tempfile read TMP/TEMP. APPDATA/LOCALAPPDATA are dropped by the scrub.
            scrubbed["USERPROFILE"] = isolatedHome;
            scrubbed["TEMP"] = isolatedTmp;
            scrubbed["TMP"] = isolatedTmp;
        }

        scrubbed["UV_CACHE_DIR"] = uvCacheDirectory;
        scrubbed["UV_PYTHON_INSTALL_DIR"] = pythonInstallDirectory;

        // Ignore any uv.toml / user configuration on the host: the committed pyproject.toml is the only configuration
        // this install is allowed to obey, and a stray index override would silently change what gets installed.
        scrubbed["UV_NO_CONFIG"] = "1";

        // uv must provision its own interpreter (ADR 0005: the host's Python is not usable).
        scrubbed["UV_PYTHON_PREFERENCE"] = "only-managed";

        // A managed interpreter must not register itself in the Windows registry (PEP 514), where the operator's
        // own tooling would discover it; uv ignores the flag on every other platform.
        scrubbed["UV_PYTHON_INSTALL_REGISTRY"] = "0";

        // Progress bars are drawn with carriage returns; without this the streamed log fills with control characters.
        scrubbed["UV_NO_PROGRESS"] = "1";
        return scrubbed;
    }

    /// <summary>The inherited keys that pass the scrub, and nothing else; the base every subprocess environment starts from.</summary>
    public static Dictionary<string, string> BuildAllowlisted()
    {
        return BuildAllowlisted(OperatingSystem.IsWindows());
    }

    // Windows environment names are case-insensitive, so "Path" and "PATH" must land on one key there.
    internal static Dictionary<string, string> BuildAllowlisted(bool isWindows)
    {
        var scrubbed = new Dictionary<string, string>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var key in isWindows ? WindowsAllowlist : UnixAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                scrubbed[key] = value;
            }
        }

        return scrubbed;
    }
}
