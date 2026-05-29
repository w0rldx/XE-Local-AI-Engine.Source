namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Worker-side AgentHome configuration (section <c>AgentHome</c>, AgentHome plan §13). Marker D introduces the
///     minimal surface the layout initializer needs; broader runtime/quota options arrive with later markers. The
///     <c>AgentHome:Sandbox</c> child section is bound separately by <c>SandboxOptions</c>.
/// </summary>
public sealed class AgentHomeOptions
{
    /// <summary>The configuration section this options type binds to.</summary>
    public const string SectionName = "AgentHome";

    /// <summary>
    ///     Whether Agent Mode features are enabled on this node. Sandbox execution is gated on this; the worker-local
    ///     layout itself can still be initialized while disabled (AgentHome plan §13).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Override for the worker-local AgentHome root. When <see langword="null" />, the root is
    ///     <c>Path.Combine(IHostEnvironment.ContentRootPath, "agent-home-state")</c> (AgentHome plan §13).
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>
    ///     How long a manifest may remain in the <c>initializing</c> state before a new run treats it as a crashed
    ///     prepare and reinitializes (AgentHome plan §6.6 rule 4). Defaults to 1800 seconds.
    /// </summary>
    public int PrepareStaleAfterSeconds { get; set; } = 1800;

    /// <summary>
    ///     The runtime profile the worker enables for AgentHome runs (AgentHome plan §12). The model may only request
    ///     the closed-enum profile from the §7 schema; the worker rejects a requested profile that is not this one.
    /// </summary>
    public string DefaultRuntimeProfile { get; set; } = "dotnet-agent-home";

    /// <summary>
    ///     Timeout for the preparation phase (sandbox attach/create, layout recovery, future workspace copy), applied
    ///     separately from the command timeout (AgentHome plan §6.1). Defaults to 900 seconds.
    /// </summary>
    public int PrepareTimeoutSeconds { get; set; } = 900;

    /// <summary>
    ///     Timeout for a single in-sandbox command, applied separately from the preparation timeout (AgentHome plan
    ///     §6.1). Defaults to 300 seconds.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 300;

    /// <summary>
    ///     Hard per-folder byte budget for a selected folder. The workspace copy (Marker F) sums the surviving
    ///     (post-exclusion) file sizes before copying; a folder over budget is reported as blocked and skipped rather
    ///     than copied (AgentHome plan §8.2 / §13). Defaults to 536870912 (512 MiB).
    /// </summary>
    public long MaxSelectedFolderBytes { get; set; } = 536870912;
}
