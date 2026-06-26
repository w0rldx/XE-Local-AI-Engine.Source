namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Narrow public seam the chat agent-mode path uses to make a conversation's uploaded attachments readable by an
///     AgentHome-capable agent's file tools (<c>list_files</c> / <c>read_file</c> / <c>search_text</c>). It re-stages the
///     node sandbox so it holds ONLY the given conversation's extracted attachments under the workspace
///     <c>attachments/</c> alias, with no cross-conversation residue.
///     <para>
///         Kept separate from the internal <see cref="IAgentHomeService" /> so the public
///         <c>NodeChatStreamService</c> can depend on it without an inconsistent-accessibility error;
///         <c>AgentHomeService</c> implements both over one shared singleton, so this re-stage shares the run-level
///         single-flight guard with <c>run_in_agent_home</c>.
///     </para>
/// </summary>
public interface IConversationSandboxStager
{
    /// <summary>
    ///     Ensures the node sandbox is freshly staged with the conversation's extracted attachments. A no-op when Agent
    ///     Mode is disabled or the conversation has no extracted files (the existing sandbox, if any, is left untouched).
    ///     The owner-node sandbox is recreated before staging so it never carries another conversation's attachments.
    /// </summary>
    Task PrepareConversationAttachmentsAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
