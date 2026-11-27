namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>No skill in the library has the id the client supplied.</summary>
public sealed class AgentSkillNotFoundException : ResourceNotFoundException
{
    public AgentSkillNotFoundException(Guid id) : base($"No skill has id '{id}'.")
    {
    }
}
