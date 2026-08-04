namespace XE_Local_AI_Engine.Client.Services.Agents.Approval;

/// <summary>
///     The identity of a SESSION-scoped approval the operator granted (see <c>ApprovalScope.Session</c>). Every one of
///     the five parts is load-bearing, and dropping any of them widens what a single "approve for this session" click
///     silently covers:
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="ConversationId" /> — the scope is the CONVERSATION, not the browser session and not the
///                 node. An approval granted in one chat must not suppress the prompt in another.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="ToolName" /> — approving a skill LOAD is not approving a resource READ. Only the two
///                 read-only skill tools are ever admitted here; <c>run_skill_script</c> is hard-excluded at the call
///                 site and can never reach this key.
///             </description>
///         </item>
///         <item>
///             <description><see cref="SkillName" /> — one skill, not every skill the agent carries.</description>
///         </item>
///         <item>
///             <description>
///                 <see cref="SkillVersion" /> — binds the approval to CONTENT. A mid-conversation edit or an import
///                 that Replaces the skill bumps the version, which invalidates the memo and re-prompts. Without it the
///                 operator's "yes" would silently carry over to content they never saw.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="ResourceName" /> — <c>null</c> for <c>load_skill</c>, the requested resource for
///                 <c>read_skill_resource</c>. Without it one approval would blanket-approve every resource in the
///                 skill, including ones the import preview never showed.
///             </description>
///         </item>
///     </list>
///     Held in memory on the invocation runner for the process lifetime and never persisted, so a node restart forgets
///     every session approval.
/// </summary>
internal readonly record struct ApprovalMemoKey(Guid ConversationId,
    string ToolName,
    string SkillName,
    int SkillVersion,
    string? ResourceName);
