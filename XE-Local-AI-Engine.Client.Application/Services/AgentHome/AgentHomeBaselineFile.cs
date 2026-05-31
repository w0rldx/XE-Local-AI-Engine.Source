namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     A baseline file (relative path under the agent-home root plus its content) written by layout initializer when the file
///     is absent. Existing files are never overwritten, so re-running initialization preserves run/workspace content.
/// </summary>
/// <param name="RelativePath">Path relative to the agent-home root.</param>
/// <param name="Content">The content to write when the file does not yet exist.</param>
internal sealed record AgentHomeBaselineFile(string RelativePath, string Content);
