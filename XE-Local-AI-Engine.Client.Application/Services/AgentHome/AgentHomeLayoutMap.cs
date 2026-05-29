namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     The canonical worker-local AgentHome directory layout (AgentHome plan §4). This set is the single source of
///     truth for both creation and partial-layout self-heal. Baseline file contents are produced by
///     <c>AgentHomeManifestService</c>. Per-run <c>/runs/&lt;run-id&gt;</c> directories are created by the run path
///     in a later marker, so only the empty <c>runs</c> root is listed here.
/// </summary>
internal static class AgentHomeLayoutMap
{
    /// <summary>Relative directories (under the agent-home root) that must exist after initialization.</summary>
    public static IReadOnlyList<string> Directories { get; } =
    [
        "workspace",
        Path.Combine("workspace", "selected"),
        "memory",
        Path.Combine("memory", "node"),
        Path.Combine("memory", "project"),
        Path.Combine("memory", "session"),
        Path.Combine("memory", "proposals"),
        "agents",
        Path.Combine("agents", "primary"),
        Path.Combine("agents", "primary", "main"),
        "skills",
        "tools",
        "artifacts",
        Path.Combine("artifacts", "test-results"),
        Path.Combine("artifacts", "reports"),
        Path.Combine("artifacts", "screenshots"),
        "patches",
        "logs",
        "runs"
    ];
}
