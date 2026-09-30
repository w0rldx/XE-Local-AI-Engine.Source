namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Which MCP client session a registered server's tool calls travel over. Stored as an int on
///     <c>McpServerRegistration</c>.
/// </summary>
/// <remarks>
///     <see cref="Shared" />, the default, is one session for every caller. <see cref="PerConversation" /> gives each
///     chat or agent conversation its own session, for a stateful server whose per-session memory (deduplication,
///     budgets, cursors) must not leak from one conversation into the next. Callers with no conversation (inbound MCP
///     runs, unattended paths) always use the shared session.
/// </remarks>
public enum McpSessionScope
{
    Shared = 0,
    PerConversation = 1
}
