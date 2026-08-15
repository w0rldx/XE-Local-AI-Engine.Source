namespace XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     Drafts agent-definition and skill content from an operator description using a NODE-LOCAL model only. The service
///     PROPOSES — it persists nothing and decides nothing: the returned draft populates the operator's form and only the
///     existing agent/skill CRUD endpoints write it. Implementations run fail-closed (an ineligible model never reaches a
///     provider) and never contend with a live invocation (see the draft admission gate).
/// </summary>
public interface IConfigDraftService
{
    /// <summary>Drafts an agent definition (name / description / instructions) from <paramref name="request" />.</summary>
    Task<DraftResult> DraftAgentDefinitionAsync(ConfigDraftRequest request, CancellationToken cancellationToken = default);

    /// <summary>Drafts a skill (MAF-safe name / description / SKILL.md body) from <paramref name="request" />.</summary>
    Task<DraftResult> DraftSkillAsync(ConfigDraftRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Whether the draft starts from nothing (<see cref="Create" />) or revises existing content.</summary>
public enum DraftMode
{
    Create = 0,
    Improve = 1
}

/// <summary>
///     One draft request. The same shape serves both surfaces: <c>ExistingContent</c> carries the agent's instructions or
///     the skill's body depending on which method is called. The existing fields are read only in
///     <see cref="DraftMode.Improve" />.
/// </summary>
public sealed record ConfigDraftRequest(
    DraftMode Mode,
    string ModelName,
    string Brief,
    string? ExistingName = null,
    string? ExistingDescription = null,
    string? ExistingContent = null);

/// <summary>
///     A normalized draft. Every field has already been trimmed, clamped to its entity cap, and — for skills — re-validated
///     against the MAF name rules; nothing here is raw model output. <see cref="GeneratedAtUtc" /> and
///     <see cref="ContentHash" /> are stamped server-side and travel back with the save so the save path can compute
///     <c>wasEdited</c> (see <see cref="DraftContentHash" /> for the canonical form both sides must agree on).
/// </summary>
public sealed record ConfigDraft(
    string Name,
    string Description,
    string Content,
    string? Rationale,
    IReadOnlyList<string> Assumptions,
    double Confidence,
    DateTimeOffset GeneratedAtUtc,
    string ContentHash);

/// <summary>
///     Why a draft did not happen. The endpoint layer maps these to status codes: <see cref="ModelNotEligible" /> and
///     <see cref="InvalidRequest" /> to 400, <see cref="NodeBusy" /> to 409, <see cref="Unparseable" /> to 422.
/// </summary>
public enum DraftFailureKind
{
    /// <summary>The model is not installed, not classified as a chat model, or its provider is not allowlisted.</summary>
    ModelNotEligible = 0,

    /// <summary>An invocation or another draft is in flight; drafts never queue.</summary>
    NodeBusy = 1,

    /// <summary>The request is missing a required field or exceeds the aggregate prompt budget.</summary>
    InvalidRequest = 2,

    /// <summary>The model returned nothing usable, or the generation budget elapsed.</summary>
    Unparseable = 3
}

/// <summary>
///     Discriminated draft outcome: exactly one of <see cref="Draft" /> or <see cref="Failure" /> is set.
///     <see cref="FailureMessage" /> is a fixed operator-facing string — model output is NEVER echoed into it.
/// </summary>
public sealed record DraftResult
{
    private DraftResult(ConfigDraft? draft, DraftFailureKind? failure, string? failureMessage)
    {
        Draft = draft;
        Failure = failure;
        FailureMessage = failureMessage;
    }

    /// <summary>The normalized draft, or <c>null</c> when <see cref="Failure" /> is set.</summary>
    public ConfigDraft? Draft { get; }

    /// <summary>The failure kind, or <c>null</c> on success.</summary>
    public DraftFailureKind? Failure { get; }

    /// <summary>A safe, fixed explanation for the failure; never contains model-emitted text.</summary>
    public string? FailureMessage { get; }

    public bool Succeeded => Draft is not null;

    public static DraftResult Success(ConfigDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new DraftResult(draft, failure: null, failureMessage: null);
    }

    public static DraftResult Failed(DraftFailureKind kind, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new DraftResult(draft: null, kind, message);
    }
}
