namespace XE_Local_AI_Engine.Client.Models;

using System.Text.Json;

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

    /// <summary>
    ///     Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c> for the active model — its chat
    ///     template renders a literal reasoning end marker, which is what llama.cpp turns into the non-empty
    ///     think-end-tag set its budget gate requires. When <c>false</c> the invocation factory omits the budget marker
    ///     entirely rather than sending a cap the server accepts and then ignores. Defaults to <c>true</c> so the
    ///     cloud/Ollama paths and pre-existing callers stay byte-identical; deliberately excluded from the config hash
    ///     (mirrors <see cref="SupportsThinking" />) because it is derived from the model, not the agent configuration.
    /// </summary>
    public bool ReasoningBudgetEnforceable { get; init; } = true;

    /// <summary>
    ///     Whether this turn runs UNATTENDED — a scheduled/headless run with no operator on the other end of an approval
    ///     round-trip. Set only by the scheduler's run-saved-agent path; the interactive chat and regeneration paths
    ///     leave it <c>false</c>. Read by the runner's single approval choke point, which fails an unattended approval
    ///     immediately instead of broadcasting a request nobody can answer and then burning the whole
    ///     <c>MaxPendingToolCallAge</c> window before timing out. Deliberately excluded from the config hash (mirrors
    ///     <see cref="SupportsThinking" /> / <see cref="SamplingOptions" />): it is a loopback-only execution-context
    ///     flag, not part of the agent's configuration, so the cross-repo encrypted/server digest stays stable and a
    ///     scheduled run hashes identically to the same agent run interactively.
    /// </summary>
    public bool IsUnattended { get; init; }

    /// <summary>
    ///     Whether the bound agent opted OUT of the send-time tool-relevance filter, so every offered tool is put in
    ///     front of the model on every round however many there are. Read by the invocation runner when it seeds the
    ///     relevance scope. Deliberately excluded from the config hash (mirrors <see cref="IsUnattended" />): the filter
    ///     narrows only the array handed to the provider — never the offer, the resolved prompt or the approval wrap —
    ///     so an agent with the filter off must hash identically to the same agent with it on, and toggling it can never
    ///     invalidate a resume.
    /// </summary>
    public bool DisableToolRelevanceFilter { get; init; }

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

    /// <summary>
    ///     OPTIONAL resolved node-local custom tools the offer carries (name + <c>Version</c> + Fixed/Parameterized mode).
    ///     Non-empty only on the loopback single-agent path when the bound agent is offered enabled custom tools; null on
    ///     every other path (mode-off fallback, orchestration, encrypted/server). Carried so the runner's session-approval
    ///     choke point can bind an "approve for session" memo to a Fixed tool's <c>Version</c> (an edit invalidates it) and
    ///     refuse a session memo for a Parameterized tool. Deliberately NOT folded into <see cref="ConfigHash" />: the
    ///     custom tools' schema/name/approval already ride <see cref="AllowedTools" /> (which IS hashed), and the
    ///     <c>Version</c> is intentionally hash-invisible so a config-hash-invisible edit still misses the version-bound
    ///     memo and re-prompts.
    /// </summary>
    public IReadOnlyList<ResolvedCustomTool>? CustomTools { get; init; }

    /// <summary>
    ///     OPTIONAL JSON schema this turn's output is CONSTRAINED to (llama-server compiles it into a GBNF grammar via
    ///     the MEAI <c>response_format</c> mapping). Set only by the benchmark judge, whose reply is parsed strictly;
    ///     every other path leaves it null, which keeps the request byte-identical to today. Deliberately excluded from
    ///     <see cref="ConfigHash" /> (mirrors <see cref="SamplingOptions" />): it is a per-send decoding constraint, not
    ///     agent configuration, so the cross-repo encrypted/server digest stays stable.
    /// </summary>
    public JsonElement? ResponseJsonSchema { get; init; }

    public required string ConfigHash { get; init; }
}
