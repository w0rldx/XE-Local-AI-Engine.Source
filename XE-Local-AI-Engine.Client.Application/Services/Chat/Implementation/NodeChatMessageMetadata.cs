namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>
///     The serialized shape of a node chat message's <c>metadata_json</c> blob. Stored as plaintext UTF-8 JSON on the
///     raw-ADO path (single-user device; see <c>NodeChatPersistenceServiceTests</c> for the documented at-rest posture).
/// </summary>
internal sealed record NodeChatMessageMetadata(
    string? MetadataJson,
    string? Reasoning,
    string? Model,
    int? InputCount,
    int? OutputCount,
    int? TotalCount,
    int? ReasoningCount,
    // Optional ordered interleave. Added after the original metadata shape, so it is the trailing member: a legacy
    // blob written before parts existed omits the key and deserializes with Parts = null (backward-compatible).
    // Stored as plaintext UTF-8 JSON (same posture as Reasoning/Model/token fields on this raw-ADO path —
    // single-user device; see NodeChatPersistenceServiceTests for the documented at-rest posture).
    IReadOnlyList<NodeChatMessagePart>? Parts = null,
    // Per-response agent attribution. Trailing members (after Parts) with null defaults, so a legacy blob written
    // before agent mode existed omits the keys and deserializes with both null (no migration). AgentName is a
    // display-name snapshot — same plaintext-on-device posture as the existing metadata fields.
    Guid? AgentDefinitionId = null,
    string? AgentName = null,
    // The reasoning effort actually used to generate this assistant turn (per-response attribution). Trailing
    // optional member with a null default, so a legacy blob written before this field existed omits the key and
    // deserializes to null (no migration). Same plaintext-on-device posture as the existing metadata fields.
    string? ReasoningEffort = null,
    // Whole-turn wall-clock generation duration in milliseconds (drives the optional tokens-per-second
    // attribution). Trailing optional member with a null default, so a legacy blob written before this field
    // existed omits the key and deserializes to null (no migration). Same plaintext-on-device posture as the
    // existing metadata fields.
    long? GenerationDurationMs = null);
