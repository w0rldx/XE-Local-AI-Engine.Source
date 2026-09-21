namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>Provider-agnostic mirror of a resolved node skill, for MAF progressive disclosure.</summary>
/// <remarks>
///     Lives in <c>.AI.Agent</c> because that project cannot reference <c>Client.Models</c>. The factory builds each
///     into a MAF <c>AgentInlineSkill</c> attached through an <c>AgentSkillsProvider</c>: instructions and resources
///     only, scripts are never registered. An imported skill's <see cref="Body" /> and resource payloads arrive ALREADY
///     FENCED by the resolver; this project never decides trust. <see cref="AllowedTools" /> is the specification's
///     space-delimited string and is metadata only — it neither grants nor restricts a tool.
/// </remarks>
public sealed class InvocationSkill
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Body { get; init; }

    public string? License { get; init; }

    public string? Compatibility { get; init; }

    public string? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public IReadOnlyList<InvocationSkillResource>? Resources { get; init; }
}

/// <summary>
///     Provider-agnostic mirror of one bundled skill resource — the level-3 payload MAF serves through
///     <c>read_skill_resource</c>.
/// </summary>
/// <remarks>
///     <see cref="Name" /> is the lookup key the model names; <see cref="MediaType" /> is carried for provenance and
///     diagnostics, because MAF's <c>AddResource</c> takes name, value and description only.
/// </remarks>
public sealed class InvocationSkillResource
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string MediaType { get; init; }

    public required string Content { get; init; }
}
