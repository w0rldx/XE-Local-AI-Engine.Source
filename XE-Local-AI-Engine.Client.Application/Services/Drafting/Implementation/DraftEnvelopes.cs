namespace XE_Local_AI_Engine.Client.Services.Drafting.Implementation;

/// <summary>
///     Structured-output envelope for an agent draft. Positional record so System.Text.Json binds by constructor
///     parameter name. BOUND-FREE by invariant 3: no <c>StringLength</c>/<c>MaxLength</c>/<c>MinLength</c> attributes —
///     the schema derived from this type reaches llama-server's grammar compiler unsanitized (only <c>options.Tools</c>
///     is sanitized), where a repetition bound fails grammar parsing. Length enforcement is post-parse, in C#.
/// </summary>
internal sealed record AgentDraftEnvelope(
    string? Name,
    string? Description,
    string? Instructions,
    string? Rationale,
    List<string>? Assumptions,
    double Confidence);

/// <summary>Structured-output envelope for a skill draft. Bound-free for the same reason as <see cref="AgentDraftEnvelope" />.</summary>
internal sealed record SkillDraftEnvelope(
    string? Name,
    string? Description,
    string? Body,
    string? Rationale,
    List<string>? Assumptions,
    double Confidence);
