namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;

internal interface IAgentToolRegistry
{
    /// <summary>
    ///     Executable local-chat tools. The invocation factory matches these by name against the offer list the
    ///     runtime package carries and passes the matches to the agent for auto-execution.
    /// </summary>
    IReadOnlyList<AITool> GetLocalChatTools();

    /// <summary>
    ///     Offer-list metadata for the same tools <see cref="GetLocalChatTools" /> exposes. The local send path uses
    ///     this to build matching <c>AllowedToolDto</c>s (name + schema + approval flag) without depending on the
    ///     executable <see cref="AIFunction" /> surface.
    /// </summary>
    IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors();
}
