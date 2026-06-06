namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Provider-agnostic mirror of a resolved node skill. Lives in <c>.AI.Agent</c> because that project cannot
///     reference <c>Client.Models</c>; the invocation runner maps the client <c>ResolvedSkill</c> record onto this one.
///     The factory builds each into a MAF <c>AgentInlineSkill(Name, Description, Body)</c> attached through an
///     <c>AgentSkillsProvider</c> so the model discovers the skill (name + description) and loads its body on demand
///     (progressive disclosure). Instructions-only: no scripts or resources in v1.
/// </summary>
public sealed record InvocationSkill(
    string Name,
    string Description,
    string Body);
