namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     Provider-agnostic description of the single agent needed to run one local or platform invocation.
/// </summary>
public sealed class InvocationAgentDefinition
{
    /// <summary>Model id passed to the underlying <see cref="IChatClient" /> for this turn.</summary>
    public required string ModelId { get; init; }

    /// <summary>System instructions prepended to <see cref="ConversationContext" />.</summary>
    public required string Instructions { get; init; }

    /// <summary>Offer-list tools projected from the runtime package before executable registry resolution.</summary>
    public required IReadOnlyList<AITool> Tools { get; init; }

    /// <summary>Prior conversation turns that should seed the agent run.</summary>
    public required IReadOnlyList<ChatMessage> ConversationContext { get; init; }

    /// <summary>Optional reasoning budget hint mapped to provider-specific chat options.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    ///     When <c>true</c> the factory attaches the Ollama-specific <c>think</c> chat option for this turn; when
    ///     <c>false</c> the option is omitted entirely. The loopback path sets this from the active model's advertised
    ///     <c>thinking</c> capability so an incapable model never receives the field (Ollama returns HTTP 400 otherwise).
    ///     Defaults to <c>true</c> so cloud providers (which ignore the unknown <c>think</c> property) keep reasoning.
    /// </summary>
    public bool SupportsThinking { get; init; } = true;

    /// <summary>
    ///     Optional developer-gated per-send sampling overrides. Null (the default) keeps the no-override path
    ///     byte-identical: the factory sets no extra chat options. When present, the factory applies only the non-null
    ///     fields as native chat options or Ollama additional properties.
    /// </summary>
    public InvocationSamplingOptions? Sampling { get; init; }

    /// <summary>
    ///     Optional resolved node skills for MAF progressive disclosure. Empty/null (the default) keeps the no-skills path
    ///     byte-identical: the factory builds the agent with the existing positional <see cref="IChatClient" /> constructor
    ///     and attaches no context provider. When non-empty, the factory builds an <c>AgentSkillsProvider</c> from these
    ///     skills and constructs the agent through the options constructor with that provider attached.
    /// </summary>
    public IReadOnlyList<InvocationSkill>? Skills { get; init; }

    /// <summary>
    ///     The launched effective context window (in tokens) of the resolved local runtime for this turn, when known.
    ///     Null (the default) keeps the byte-identical no-override path. When set AND the per-send
    ///     <see cref="InvocationSamplingOptions.NumCtx" /> is not, the factory writes it as the <c>num_ctx</c> chat option so
    ///     the inner provider-round budgeter sizes against the real window; a per-send <c>num_ctx</c> still wins.
    /// </summary>
    public int? EffectiveContextTokens { get; init; }

    /// <summary>
    ///     Optional JSON schema this turn's output is CONSTRAINED to. Null (the default) keeps the unconstrained path
    ///     byte-identical: the factory sets no <see cref="ChatOptions.ResponseFormat" />, so no <c>response_format</c>
    ///     reaches the wire. When set, the factory maps it through <see cref="ChatResponseFormat.ForJsonSchema" />, which
    ///     the MEAI OpenAI adapter emits at <c>response_format.json_schema.schema</c> — the only path llama-server reads
    ///     before compiling it into a GBNF grammar. The CALLER owns keeping the schema free of repetition bounds
    ///     (<c>minLength</c>/<c>maxLength</c>/<c>pattern</c>/<c>minItems</c>/<c>maxItems</c>), which that grammar rejects.
    /// </summary>
    public JsonElement? ResponseJsonSchema { get; init; }

    /// <summary>
    ///     Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c> for <see cref="ModelId" /> —
    ///     that is, whether its chat template renders a literal reasoning end marker. When <c>false</c> the factory omits
    ///     the budget marker: llama.cpp would accept the field and silently ignore it, so sending it would only claim a cap
    ///     that does not exist. Read only when <see cref="SupportsThinking" /> is <c>true</c> (a budget is emitted
    ///     exclusively on the graded branch). Defaults to <c>true</c> so cloud providers and pre-existing callers keep the
    ///     byte-identical request they had before this flag existed.
    /// </summary>
    public bool ReasoningBudgetEnforceable { get; init; } = true;
}
