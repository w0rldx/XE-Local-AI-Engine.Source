namespace XE_Local_AI_Engine.Client.Services.Agents.Approval;

/// <summary>
///     The identity of a SESSION-scoped approval the operator granted (see <c>ApprovalScope.Session</c>).
/// </summary>
/// <remarks>
///     All five parts are load-bearing: dropping any one widens what a single "approve for this session" click
///     covers — the conversation, not the node; the tool, since a skill LOAD is not a resource READ; the one skill;
///     the version, which binds the approval to CONTENT; and the resource, or one approval blankets every file the
///     skill carries. Why each, and the tool allowlist: docs/wiki/04-agent-mode.md ("Approval scoping"). Held in
///     memory on the runner and never persisted, so a node restart forgets every session approval.
/// </remarks>
internal readonly record struct ApprovalMemoKey(
    Guid ConversationId,
    string ToolName,
    string SkillName,
    int SkillVersion,
    string? ResourceName);
