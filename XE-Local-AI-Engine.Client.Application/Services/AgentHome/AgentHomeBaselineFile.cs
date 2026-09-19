namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     A baseline file (relative path under the agent-home root plus its content) written by layout initializer when the file
///     is absent. Existing files are never overwritten, so re-running initialization preserves run/workspace content.
/// </summary>
internal sealed class AgentHomeBaselineFile
{
    /// <summary>Path relative to the agent-home root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The content to write when the file does not yet exist.</summary>
    public required string Content { get; init; }
}
