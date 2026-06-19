namespace XE_Local_AI_Engine.Client.Models;

public sealed record RuntimePackage
{
    public required Guid InvocationId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid ClientNodeId { get; init; }

    public required int AgentDefinitionVersion { get; init; }

    public required string ResolvedSystemPrompt { get; init; }

    public required List<ConversationMessageDto> ConversationContext { get; init; }

    public required List<AllowedToolDto> AllowedTools { get; init; }

    public Dictionary<string, object>? ToolPolicies { get; init; }

    public string? ModelProfile { get; init; }

    public string? ReasoningEffort { get; init; }

    /// <summary>
    ///     Developer-gated per-send sampling overrides (temperature, top-p, min-p, num_ctx, …). Null when no overrides
    ///     were requested, which keeps the no-override path byte-identical to today. Deliberately excluded from the
    ///     config hash (mirrors <see cref="SupportsThinking" />): sampling is a loopback-only per-send knob, so the
    ///     cross-repo encrypted/server digest stays stable. Threaded to the invocation factory, which sets the matching
    ///     Ollama chat options only for the non-null fields.
    /// </summary>
    public SamplingOptions? SamplingOptions { get; init; }

    /// <summary>
    ///     Whether the active model advertises the Ollama <c>thinking</c> capability. Threaded to the invocation factory
    ///     so the <c>think</c> chat option is attached only for a capable model (an incapable model returns HTTP 400 for
    ///     any <c>think</c> value). Defaults to <c>true</c> so the cloud/non-Ollama path and pre-existing callers stay
    ///     byte-identical; deliberately excluded from the config hash so capable models keep a stable hash.
    /// </summary>
    public bool SupportsThinking { get; init; } = true;

    public List<string>? RequestedCapabilities { get; init; }

    public required TimeoutSettings Timeouts { get; init; }

    /// <summary>
    ///     OPTIONAL compiled orchestration spec (orchestration). Non-null only on the loopback path when the bound definition
    ///     is a tool-capable orchestrator; the invocation runner branches to the workflow drive when this is set. Null
    ///     on the single-agent loopback path and on the encrypted/server path, where the config hash is byte-identical
    ///     to the payload before orchestration was added.
    /// </summary>
    public OrchestrationSpec? OrchestrationSpec { get; init; }

    /// <summary>
    ///     OPTIONAL resolved, decrypted skill set for MAF progressive disclosure (agent skills). Non-empty only on the
    ///     loopback path when the bound definition assigns enabled skills; the invocation factory builds these into an
    ///     <c>AgentSkillsProvider</c>. Null/empty on the no-skills loopback path and on the encrypted/server path, where
    ///     the config hash stays byte-identical to the pre-skills payload (folded WhenWritingNull, same posture as
    ///     <see cref="OrchestrationSpec" />). The bodies are NOT in <see cref="ResolvedSystemPrompt" /> (progressive
    ///     disclosure loads them on demand), so the runtime-package builder folds this set into the config hash.
    /// </summary>
    public IReadOnlyList<ResolvedSkill>? Skills { get; init; }

    public required string ConfigHash { get; init; }
}
