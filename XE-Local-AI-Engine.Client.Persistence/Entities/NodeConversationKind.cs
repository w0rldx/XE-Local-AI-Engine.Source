namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The <c>conversations.kind</c> discriminator: what created a conversation, and therefore whether the chat list
///     should show it. Shaped on <see cref="NodeChatOrigin" />, minus its <c>All</c> set — nothing needs a kind set
///     yet, and <c>NodeChatOrigin.All</c> has no consumer anywhere in the tree.
/// </summary>
/// <remarks>
///     <b>Public on purpose, and not because the Application readers need it.</b> Persistence already grants that
///     assembly its internals (<c>Properties/AssemblyInfo.cs</c>), so the raw-SQL readers alone would not force this.
///     The constraint is one assembly further out: <c>XE-Local-AI-Engine.Tests</c> is absent from that
///     <c>InternalsVisibleTo</c> list and holds the conversation-kind tests. Narrowing this back to <c>internal</c>
///     breaks only that project, at the end of a long Release build.
/// </remarks>
public static class NodeConversationKind
{
    /// <summary>An ordinary chat the operator started. The column default, and the only kind the chat list returns.</summary>
    public const string Chat = "chat";

    /// <summary>The transcript owned by an <see cref="AgentWorkSession" />.</summary>
    public const string WorkSession = "work-session";

    /// <summary>The transcript owned by an <see cref="IntegrationSession" />.</summary>
    public const string Integration = "integration";
}
