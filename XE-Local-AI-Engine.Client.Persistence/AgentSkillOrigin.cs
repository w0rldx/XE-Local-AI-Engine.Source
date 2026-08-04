namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Provenance of an agent skill. <see cref="Local" /> is operator-authored content typed into the skill editor;
///     <see cref="Imported" /> came from a third party (an uploaded archive or a GitHub repository) and is therefore
///     attacker-controlled text. The distinction is load-bearing at runtime, not cosmetic: an imported body and every
///     one of its resources are wrapped in the untrusted-content fence before they reach the model, and session-scoped
///     approval is withheld for them. A row can be promoted to <see cref="Imported" /> but never demoted back to
///     <see cref="Local" /> — see <c>IAgentSkillStore.UpdateAsync</c>.
/// </summary>
public enum AgentSkillOrigin
{
    Local = 0,
    Imported = 1
}
