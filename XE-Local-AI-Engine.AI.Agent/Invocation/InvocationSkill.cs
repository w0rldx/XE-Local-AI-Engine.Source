namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Provider-agnostic mirror of a resolved node skill. Lives in <c>.AI.Agent</c> because that project cannot
///     reference <c>Client.Models</c>; the invocation runner maps the client <c>ResolvedSkill</c> record onto this one.
///     The factory builds each into a MAF <c>AgentInlineSkill</c> — the full frontmatter constructor plus one
///     <c>AddResource</c> per <see cref="Resources" /> entry — attached through an <c>AgentSkillsProvider</c> so the
///     model discovers the skill (name + description), loads its body on demand, and reads a bundled resource only when
///     it asks for one (progressive disclosure). Instructions and resources only: scripts are never registered.
///     <para>
///         An imported skill's <see cref="Body" /> and resource payloads arrive here ALREADY FENCED by the resolver's
///         untrusted-content wrap. This project never decides trust; it renders what it is handed.
///     </para>
///     <para>
///         <see cref="AllowedTools" /> is the specification's space-delimited string (MAF's frontmatter takes it in that
///         form). It is metadata only — it neither grants nor restricts a tool.
///     </para>
/// </summary>
public sealed record InvocationSkill(
    string Name,
    string Description,
    string Body,
    string? License = null,
    string? Compatibility = null,
    string? AllowedTools = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<InvocationSkillResource>? Resources = null);

/// <summary>
///     Provider-agnostic mirror of one bundled skill resource — the level-3 payload MAF serves through
///     <c>read_skill_resource</c>. <see cref="Name" /> is the lookup key the model names; <see cref="MediaType" /> is
///     carried for provenance and diagnostics (MAF's <c>AddResource</c> takes name, value and description only).
/// </summary>
public sealed record InvocationSkillResource(
    string Name,
    string Description,
    string MediaType,
    string Content);
