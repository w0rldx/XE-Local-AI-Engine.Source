namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using XE_Local_AI_Engine.Providers.Python.Implementation;

/// <summary>
///     Builds the scrubbed environments the training subprocesses run under, each on top of
///     <see cref="ManagedPythonEnvironment.BuildAllowlisted" />: ONLY the allow-listed keys and the ones named here pass
///     through, and everything else is dropped by construction.
/// </summary>
/// <remarks>
///     The environment uv itself runs under is <see cref="ManagedPythonEnvironment.BuildUvEnvironment" />. See
///     docs/wiki/18-training.md ("The scrubbed environments, and the uv pipeline the compute tool shares").
/// </remarks>
internal static class TrainingRuntimeEnvironment
{
    /// <summary>
    ///     The environment for short read-only probes (<c>nvidia-smi</c>, <c>probe.py</c>). <paramref name="isolatedHome" />
    ///     must be a directory this process owns — never the shared temp directory, which any local user could plant a
    ///     torch or matplotlib config into.
    /// </summary>
    public static Dictionary<string, string> BuildProbeEnvironment(string isolatedHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isolatedHome);

        var scrubbed = ManagedPythonEnvironment.BuildAllowlisted();

        // Keeps torch/unsloth from writing compilation caches wherever HOME happens to point; an absent HOME makes some
        // libraries fall back to the current working directory instead.
        scrubbed["HOME"] = isolatedHome;
        return scrubbed;
    }

    /// <summary>
    ///     The environment for a training run: every default cache this stack writes to is pointed somewhere this node
    ///     owns, none of those defaults being writable or wanted under a scrubbed environment.
    /// </summary>
    /// <remarks>
    ///     Run-scoped state lives under <paramref name="workDirectory" /> and dies with the run's <c>work/</c> sweep,
    ///     while compiled Triton and Inductor kernels live under the machine-global <paramref name="cacheRoot" />. The
    ///     three offline flags are what actually guarantee no network call: several <c>huggingface_hub</c> paths inside
    ///     unsloth never thread <c>local_files_only</c> through. See docs/wiki/18-training.md ("The scrubbed
    ///     environments, and the uv pipeline the compute tool shares").
    /// </remarks>
    public static Dictionary<string, string> BuildTrainEnvironment(string cacheRoot, string workDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        var scrubbed = ManagedPythonEnvironment.BuildAllowlisted();
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

    /// <summary>
    ///     The environment for an export subprocess — the merge step and the llama.cpp conversion scripts alike: the
    ///     same containment as <see cref="BuildTrainEnvironment" />, plus the vendored <c>gguf-py</c> on
    ///     <c>PYTHONPATH</c>.
    /// </summary>
    /// <remarks>
    ///     The conversion scripts resolve that package relative to the llama.cpp repository they normally live in,
    ///     which the provisioned script tree deliberately is not.
    /// </remarks>
    public static Dictionary<string, string> BuildExportEnvironment(string cacheRoot, string workDirectory, string ggufPyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ggufPyDirectory);

        var scrubbed = BuildTrainEnvironment(cacheRoot, workDirectory);
        scrubbed["PYTHONPATH"] = ggufPyDirectory;
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
}
