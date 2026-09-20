namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Worker-side AgentHome configuration (section <c>AgentHome</c>) limited to settings consumed by the layout
///     initializer. The <c>AgentHome:Sandbox</c> child section is bound separately by <c>SandboxOptions</c>.
/// </summary>
public sealed class AgentHomeOptions
{
    public const string SectionName = "AgentHome";

    /// <summary>
    ///     Whether Agent Mode features are enabled on this node. Sandbox execution is gated on this; the worker-local
    ///     layout itself can still be initialized while disabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Override for the worker-local AgentHome root. When <see langword="null" />, the root is
    ///     <c>Path.Combine(INodeDataDirectory.Root, "agent-home-state")</c>.
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>
    ///     How long a manifest may remain in the <c>initializing</c> state before a new run treats it as a crashed
    ///     prepare and reinitializes. Defaults to 1800 seconds.
    /// </summary>
    public int PrepareStaleAfterSeconds { get; set; } = 1800;

    /// <summary>
    ///     The runtime profile the worker enables for AgentHome runs. The model may only request
    ///     the closed-enum profile from the tool schema; the worker rejects a requested profile that is not this one.
    /// </summary>
    public string DefaultRuntimeProfile { get; set; } = "dotnet-agent-home";

    /// <summary>
    ///     Timeout for the preparation phase (sandbox attach/create and layout recovery), applied
    ///     separately from the command timeout. Defaults to 900 seconds.
    /// </summary>
    public int PrepareTimeoutSeconds { get; set; } = 900;

    /// <summary>
    ///     Timeout for a single in-sandbox command, applied separately from the preparation timeout.
    ///     Defaults to 300 seconds.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 300;

    /// <summary>
    ///     Wall-clock ceiling for the whole goal-execution loop — every inner model turn and every command it runs.
    ///     Defaults to 600 seconds.
    /// </summary>
    /// <remarks>
    ///     Applies on top of, and separately from, <see cref="CommandTimeoutSeconds" />, which still bounds ONE
    ///     command. A stuck inner loop holds the node's single inference slot for as long as it runs, so the ceiling
    ///     is not optional. The default is a conservative starting point, not a measured value: re-set it from a
    ///     timed, realistic multi-tool-call goal on the target model class.
    /// </remarks>
    public int MaxRunSeconds { get; set; } = 600;

    /// <summary>
    ///     Maximum number of inner tool calls one goal-execution loop may make. Defaults to 24.
    /// </summary>
    /// <remarks>
    ///     The model is told the budget, and the call that would exceed it is refused rather than executed. The run
    ///     then finishes and still exports whatever partial work it produced.
    /// </remarks>
    public int MaxInnerToolCalls { get; set; } = 24;

    /// <summary>
    ///     Hard byte budget for ONE <c>write_file</c> inside the goal-execution loop. Defaults to 262144 (256 KiB).
    /// </summary>
    public int MaxWriteFileBytes { get; set; } = 262144;

    /// <summary>
    ///     Hard byte budget for ALL <c>write_file</c> calls in one run, summed. Defaults to 4194304 (4 MiB). The
    ///     sandbox's own jail-disk ceiling still bounds what a command writes; this bounds what the MODEL writes
    ///     directly.
    /// </summary>
    public long MaxTotalWriteBytes { get; set; } = 4194304;

    /// <summary>
    ///     How many bytes of one command's combined stdout/stderr are fed back into the inner model's context. The
    ///     capture itself is bounded by the provider; this is the smaller, context-sized budget. Defaults to 16384.
    /// </summary>
    public int MaxCommandOutputBytes { get; set; } = 16384;

    /// <summary>
    ///     Hard per-folder byte budget for a selected folder. Workspace copy sums the surviving
    ///     (post-exclusion) file sizes before copying; a folder over budget is reported as blocked and skipped rather
    ///     than copied. Defaults to 536870912 (512 MiB).
    /// </summary>
    public long MaxSelectedFolderBytes { get; set; } = 536870912;

    /// <summary>
    ///     Hard byte budget for an exported patch. Patch export measures the captured
    ///     <c>changes.patch</c>; a patch over budget is reported as blocked and not written, while the
    ///     <c>changed-files.json</c> metadata is still kept. Defaults to 52428800 (50 MiB).
    /// </summary>
    public long MaxPatchBytes { get; set; } = 52428800;

    /// <summary>
    ///     The AgentHome tool-capability allowlist: the model ids the worker considers tool-capable. Defaults to
    ///     <c>["qwen3:8b"]</c>.
    /// </summary>
    /// <remarks>
    ///     The loopback offer list omits <c>run_in_agent_home</c> when the active model id is not in this list. The
    ///     encrypted path stays server-gated by <c>AiModel.SupportsToolCalling</c>.
    /// </remarks>
    public IReadOnlyList<string> ToolCapableModels { get; set; } = ["qwen3:8b"];

    /// <summary>
    ///     Whether host patch apply may apply a binary change. When
    ///     <see langword="false" /> (the default), a patch containing a binary block is rejected outright — binary
    ///     content never touches the host. When flipped on, binary changes apply via git's <c>--binary</c> literal form.
    /// </summary>
    public bool AllowBinaryPatchApply { get; set; }

    /// <summary>
    ///     Timeout for a single host <c>git apply</c> invocation during host patch apply,
    ///     applied separately from the in-sandbox command timeout. Defaults to 120 seconds.
    /// </summary>
    public int PatchApplyTimeoutSeconds { get; set; } = 120;
}
