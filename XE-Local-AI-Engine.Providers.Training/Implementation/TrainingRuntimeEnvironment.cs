namespace XE_Local_AI_Engine.Providers.Training.Implementation;

/// <summary>
///     Builds the scrubbed, allow-listed environments the training subprocesses run under. ONLY the keys named here pass
///     through; everything else — <c>LD_PRELOAD</c>, <c>LD_LIBRARY_PATH</c>, proxy and credential variables, and every
///     node secret — is dropped by construction, and <see cref="LinuxTrainingProcessRunner" /> clears the inherited
///     environment before applying the result.
/// </summary>
internal static class TrainingRuntimeEnvironment
{
    private static readonly string[] Allowlist = ["PATH", "LANG", "LC_ALL", "CUDA_HOME", "CUDA_PATH"];

    /// <summary>
    ///     The environment for uv itself. uv is pointed at isolated HOME/TMPDIR and cache/interpreter directories under
    ///     the training cache root, so the install neither reads the operator's <c>~/.config/uv</c> (which could redirect
    ///     an index) nor scatters gigabytes into the user's home.
    /// </summary>
    public static Dictionary<string, string> BuildUvEnvironment(string isolatedHome, string isolatedTmp, string uvCacheDirectory, string pythonInstallDirectory)
    {
        var scrubbed = BuildAllowlisted();
        scrubbed["HOME"] = isolatedHome;
        scrubbed["TMPDIR"] = isolatedTmp;
        scrubbed["UV_CACHE_DIR"] = uvCacheDirectory;
        scrubbed["UV_PYTHON_INSTALL_DIR"] = pythonInstallDirectory;

        // Ignore any uv.toml / user configuration on the box: the committed pyproject.toml is the only configuration
        // this install is allowed to obey, and a stray index override would silently change what gets installed.
        scrubbed["UV_NO_CONFIG"] = "1";

        // uv must provision its own interpreter (ADR 0005: the host's Python is not usable).
        scrubbed["UV_PYTHON_PREFERENCE"] = "only-managed";

        // Progress bars are drawn with carriage returns; without this the streamed log fills with control characters.
        scrubbed["UV_NO_PROGRESS"] = "1";
        return scrubbed;
    }

    /// <summary>
    ///     The environment for short read-only probes (<c>nvidia-smi</c>, <c>probe.py</c>). <paramref name="isolatedHome" />
    ///     must be a directory this process owns — never the shared temp directory, which any local user could plant a
    ///     torch or matplotlib config into.
    /// </summary>
    public static Dictionary<string, string> BuildProbeEnvironment(string isolatedHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isolatedHome);

        var scrubbed = BuildAllowlisted();

        // Keeps torch/unsloth from writing compilation caches wherever HOME happens to point; an absent HOME makes some
        // libraries fall back to the current working directory instead.
        scrubbed["HOME"] = isolatedHome;
        return scrubbed;
    }

    private static Dictionary<string, string> BuildAllowlisted()
    {
        var scrubbed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Allowlist)
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
