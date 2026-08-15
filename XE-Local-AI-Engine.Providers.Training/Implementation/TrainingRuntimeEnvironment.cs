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

    /// <summary>
    ///     The environment for a training run. Every library on this stack writes caches somewhere by default —
    ///     <c>~/.cache/huggingface</c>, <c>/tmp/torchinductor_&lt;user&gt;</c>, a CWD-relative
    ///     <c>unsloth_compiled_cache</c> — and none of those defaults are writable or wanted under the scrubbed
    ///     environment, so each one is pointed somewhere this node owns.
    /// </summary>
    /// <remarks>
    ///     The split is deliberate: run-scoped state (HOME, TMPDIR, the HF cache) lives under
    ///     <paramref name="workDirectory" /> and dies with the run's <c>work/</c> sweep, while compiled Triton/Inductor
    ///     kernels live under the machine-global <paramref name="cacheRoot" /> so the second run on a box does not pay
    ///     the compile cost again. The three offline flags are what actually guarantee no network call: several
    ///     <c>huggingface_hub</c> paths inside unsloth never thread <c>local_files_only</c> through.
    /// </remarks>
    public static Dictionary<string, string> BuildTrainEnvironment(string cacheRoot, string workDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        var scrubbed = BuildAllowlisted();
        scrubbed["HOME"] = Path.Combine(workDirectory, ".home");
        scrubbed["TMPDIR"] = Path.Combine(workDirectory, ".tmp");
        scrubbed["HF_HOME"] = Path.Combine(workDirectory, "hf-cache");

        var compileCaches = Path.Combine(cacheRoot, "caches");
        scrubbed["XDG_CACHE_HOME"] = compileCaches;
        scrubbed["TORCHINDUCTOR_CACHE_DIR"] = Path.Combine(compileCaches, "inductor");
        scrubbed["TRITON_CACHE_DIR"] = Path.Combine(compileCaches, "triton");
        scrubbed["UNSLOTH_COMPILE_LOCATION"] = Path.Combine(compileCaches, "unsloth");

        // Offline, at all three layers that have their own flag.
        scrubbed["HF_HUB_OFFLINE"] = "1";
        scrubbed["TRANSFORMERS_OFFLINE"] = "1";
        scrubbed["HF_DATASETS_OFFLINE"] = "1";

        scrubbed["UNSLOTH_DISABLE_AUTO_UPDATES"] = "1";
        scrubbed["HF_HUB_DISABLE_TELEMETRY"] = "1";

        // The trainer forks dataloader workers after the Rust tokenizer has gone multi-threaded; without this the fork
        // is a deadlock risk and, at best, a warning per worker.
        scrubbed["TOKENIZERS_PARALLELISM"] = "false";

        // report_to="none" in SFTConfig is the primary mechanism; these cover third-party code that reads the env.
        scrubbed["WANDB_DISABLED"] = "true";
        scrubbed["WANDB_MODE"] = "disabled";
        return scrubbed;
    }

    /// <summary>The directories <see cref="BuildTrainEnvironment" /> points at that must exist before the spawn.</summary>
    public static IReadOnlyList<string> TrainEnvironmentDirectories(string cacheRoot, string workDirectory) =>
    [
        Path.Combine(workDirectory, ".home"),
        Path.Combine(workDirectory, ".tmp"),
        Path.Combine(workDirectory, "hf-cache"),
        Path.Combine(cacheRoot, "caches")
    ];

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
