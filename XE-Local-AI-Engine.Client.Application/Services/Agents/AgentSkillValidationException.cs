namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>Thrown when a skill create/update fails validation. The message is safe to surface to callers (no skill body/description).</summary>
public sealed class AgentSkillValidationException : Exception
{
    public AgentSkillValidationException(string message) : base(message)
    {
    }

    public AgentSkillValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
