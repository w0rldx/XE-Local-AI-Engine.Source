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
    ///     <c>false</c> the option is omitted entirely. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    ///     The loopback path sets this from the active model's advertised <c>thinking</c> capability, so an incapable
    ///     model never receives the field (Ollama returns HTTP 400 otherwise). The default keeps reasoning on for cloud
    ///     providers, which ignore the unknown <c>think</c> property.
    /// </remarks>
    public bool SupportsThinking { get; init; } = true;

    /// <summary>Optional developer-gated per-send sampling overrides.</summary>
    /// <remarks>
    ///     Null (the default) keeps the no-override path byte-identical: the factory sets no extra chat options. When
    ///     present, only the non-null fields are applied, as native chat options or Ollama additional properties.
    /// </remarks>
    public InvocationSamplingOptions? Sampling { get; init; }

    /// <summary>Optional resolved node skills for MAF progressive disclosure.</summary>
    /// <remarks>
    ///     Empty or null (the default) keeps the no-skills path byte-identical: the factory uses the positional
    ///     <see cref="IChatClient" /> constructor and attaches no context provider. When non-empty it builds an
    ///     <c>AgentSkillsProvider</c> from these skills and constructs the agent through the options constructor.
    /// </remarks>
    public IReadOnlyList<InvocationSkill>? Skills { get; init; }

    /// <summary>
    ///     The launched effective context window, in tokens, of the resolved local runtime for this turn, when known.
    /// </summary>
    /// <remarks>
    ///     Null (the default) keeps the byte-identical no-override path. When set AND the per-send
    ///     <see cref="InvocationSamplingOptions.NumCtx" /> is not, the factory writes it as the <c>num_ctx</c> chat
    ///     option so the inner provider-round budgeter sizes against the real window; a per-send <c>num_ctx</c> wins.
    /// </remarks>
    public int? EffectiveContextTokens { get; init; }

    /// <summary>Optional JSON schema this turn's output is CONSTRAINED to.</summary>
    /// <remarks>
    ///     Null (the default) sets no <see cref="ChatOptions.ResponseFormat" />, so no <c>response_format</c> reaches
    ///     the wire. When set it is mapped through <see cref="ChatResponseFormat.ForJsonSchema" />, which the MEAI
    ///     OpenAI adapter emits at <c>response_format.json_schema.schema</c> — the only path llama-server reads before
    ///     compiling it into a GBNF grammar. The CALLER owns keeping the schema free of the repetition bounds
    ///     (<c>minLength</c>, <c>maxLength</c>, <c>pattern</c>, <c>minItems</c>, <c>maxItems</c>) that grammar rejects.
    /// </remarks>
    public JsonElement? ResponseJsonSchema { get; init; }

    /// <summary>
    ///     Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c> for <see cref="ModelId" /> —
    ///     that is, whether its chat template renders a literal reasoning end marker. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    ///     When <c>false</c> the factory omits the budget marker: llama.cpp would accept the field and silently ignore
    ///     it, so sending it would claim a cap that does not exist. Read only when <see cref="SupportsThinking" /> is
    ///     <c>true</c>, since a budget is emitted exclusively on the graded branch; the default keeps cloud providers
    ///     and pre-existing callers byte-identical.
    /// </remarks>
    public bool ReasoningBudgetEnforceable { get; init; } = true;
}
