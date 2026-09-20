namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>
///     The serialized shape of a node chat message's <c>metadata_json</c> blob: UTF-8 JSON in memory, which the
///     raw-ADO path encrypts at rest through <c>NodeChatDbContext.EncryptMessageMetadata</c> under per-record AAD.
/// </summary>
internal sealed record NodeChatMessageMetadata(
    string? MetadataJson,
    string? Reasoning,
    string? Model,
    int? InputCount,
    int? OutputCount,
    int? TotalCount,
    int? ReasoningCount,
    // Optional ordered interleave, a trailing member so a legacy blob omits the key and deserializes to null. It
    // rides the same blob, so it carries the same at-rest encryption as the other fields on this raw-ADO path.
    IReadOnlyList<NodeChatMessagePart>? Parts = null,
    // Per-response agent attribution: trailing members with null defaults, so a legacy blob omits the keys and needs
    // no migration. AgentName is a display-name snapshot, in the same encrypted blob.
    Guid? AgentDefinitionId = null,
    string? AgentName = null,
    // The reasoning effort that actually generated this assistant turn: a trailing optional member, so a legacy blob
    // omits the key and needs no migration, in the same encrypted blob.
    string? ReasoningEffort = null,
    // Whole-turn wall-clock generation duration in milliseconds, driving the optional tokens-per-second attribution:
    // a trailing optional member, so a legacy blob omits the key, in the same encrypted blob.
    long? GenerationDurationMs = null,
    // Knowledge-base sources that grounded this plain-chat turn: a trailing optional member, so a legacy blob omits
    // the key. Only NON-SENSITIVE provenance rides here — ids, derived title and section, score, no chunk body.
    IReadOnlyList<NodeChatMessageSource>? Sources = null);
