namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     A resolved, decrypted skill from the node skill library that is assigned to and enabled for the bound agent. Built
///     into a MAF <c>AgentInlineSkill</c> (name + description + body-as-instructions) at the invocation factory and
///     offered to the model via progressive disclosure. <see cref="Description" /> and <see cref="Body" /> are plaintext
///     here (the skill store decrypts on read); the runtime-package config hash folds the body's SHA-256, never the body
///     itself, so a body edit/rename/picklist change invalidates resume without ever placing plaintext skill content in
///     the canonical hash payload. Lives in <c>Client.Models</c> (a pure data DTO, like <c>OrchestrationSpec</c>) so the
///     runtime package and the config hash can carry it without inverting the Models -&gt; Services dependency direction.
/// </summary>
public sealed record ResolvedSkill(
    Guid Id,
    string Name,
    string Description,
    string Body,
    int Version);
