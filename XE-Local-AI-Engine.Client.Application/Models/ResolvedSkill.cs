namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     A resolved, decrypted skill from the node skill library that is assigned to and enabled for the bound agent. Built
///     into a MAF <c>AgentInlineSkill</c> (name + description + body-as-instructions, plus the optional frontmatter and
///     the bundled resources) at the invocation factory and offered to the model via progressive disclosure.
///     <see cref="Description" />, <see cref="Body" /> and every resource payload are plaintext here (the skill store
///     decrypts on read); the runtime-package config hash folds the body's SHA-256, never the body itself, so a body
///     edit/rename/picklist change invalidates resume without ever placing plaintext skill content in the canonical hash
///     payload. Lives in <c>Client.Models</c> (a pure data DTO, like <c>OrchestrationSpec</c>) so the runtime package and
///     the config hash can carry it without inverting the Models -&gt; Services dependency direction.
///     <para>
///         <c>IsImported</c> records whether the skill came from a third-party import rather than from the operator's
///         own authoring. It is one bit rather than the persistence-layer origin enum so <c>Client.Models</c> keeps no
///         dependency on <c>Client.Persistence</c>; the resolver maps <c>AgentSkillOrigin.Imported</c> onto it. Its one
///         runtime effect is that an imported skill is never eligible for a SESSION-scoped approval — skill names are
///         attacker-chosen, and a durable approval granted on a phished name is the worst available outcome. Defaults
///         to <c>false</c>, which keeps every pre-import call site, and every locally authored skill, on today's
///         behaviour.
///     </para>
///     <para>
///         An imported skill arrives here with its <see cref="Body" /> and every resource payload ALREADY WRAPPED in the
///         untrusted-content fence (see <c>AgentDefinitionResolver.ResolveSkillsAsync</c>). The fence is applied at that
///         single choke point rather than at each consumer, so the fenced bytes are what the config hash folds and what
///         every skills consumer — invocation factory and sub-agent spawn alike — hands to MAF.
///     </para>
///     <para>
///         <see cref="AllowedTools" /> is the specification's space-delimited frontmatter string, carried verbatim. It is
///         METADATA ONLY: the standard defines it as pre-approval, not restriction, so it grants nothing and restricts
///         nothing here — the tighten-only approval policy remains the sole authority over what a tool requires.
///     </para>
/// </summary>
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
///     One bundled level-3 file of a resolved skill (the specification's <c>references/</c> / <c>assets/</c> payload),
///     served to the model on demand through MAF's <c>read_skill_resource</c>. <see cref="Name" /> is the
///     skill-root-relative path the model looks the file up by; <see cref="Content" /> is the decrypted payload — fenced
///     when the owning skill was imported, verbatim when the operator authored it.
/// </summary>
public sealed record ResolvedSkillResource(
    string Name,
    string Description,
    string MediaType,
    string Content);
