namespace XE_Local_AI_Engine.Client.Models;

// A pure data DTO in Client.Models (like OrchestrationSpec) with no Client.Persistence reference — which is why IsImported is one bit and not
// the origin enum — so the runtime package and the config hash carry it without inverting the Models -> Services dependency direction.
/// <summary>
///     A resolved, decrypted skill from the node skill library, assigned to and enabled for the bound agent, built into a MAF <c>AgentInlineSkill</c> at the invocation factory
///     and offered to the model via progressive disclosure.
/// </summary>
/// <remarks>
///     <see cref="Description" />, <see cref="Body" /> and every resource payload are plaintext here (the store decrypts on read); the config hash folds the body's SHA-256, never
///     the body, so an edit, rename or picklist change invalidates resume with no plaintext in the hash payload. An imported skill is never SESSION-approvable: skill names are
///     attacker-chosen, and a durable approval on a phished name is the worst available outcome. <see cref="AllowedTools" /> is METADATA ONLY — pre-approval, not restriction,
///     so the tighten-only policy stays the sole authority.
/// </remarks>
/// <param name="Body">Skill instructions, plaintext; when imported, ALREADY fenced by <c>AgentDefinitionResolver.ResolveSkillsAsync</c> — the one choke point all consumers read through.</param>
/// <param name="IsImported">Whether the skill came from a third-party import rather than operator authoring; the resolver maps <c>AgentSkillOrigin.Imported</c> onto it.</param>
/// <param name="AllowedTools">The specification's space-delimited frontmatter string, carried verbatim.</param>
public sealed record ResolvedSkill(
    Guid Id,
    string Name,
    string Description,
    string Body,
    int Version,
    bool IsImported = false,
    string? License = null,
    string? Compatibility = null,
    string? AllowedTools = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<ResolvedSkillResource>? Resources = null);

/// <summary>
///     One bundled level-3 file of a resolved skill (the specification's <c>references/</c> / <c>assets/</c> payload), served to the model
///     on demand through MAF's <c>read_skill_resource</c>.
/// </summary>
/// <remarks>
///     <see cref="Name" /> is the skill-root-relative path the model looks the file up by; <see cref="Content" /> is the decrypted payload
///     — fenced when the owning skill was imported, verbatim when the operator authored it.
/// </remarks>
public sealed record ResolvedSkillResource(
    string Name,
    string Description,
    string MediaType,
    string Content);
