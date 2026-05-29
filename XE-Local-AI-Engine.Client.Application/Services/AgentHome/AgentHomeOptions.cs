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
}
