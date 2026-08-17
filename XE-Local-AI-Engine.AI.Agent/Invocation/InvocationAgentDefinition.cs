namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     Provider-agnostic description of the single agent needed to run one local or platform invocation.
/// </summary>
/// <param name="ModelId">Model id passed to the underlying <see cref="IChatClient" /> for this turn.</param>
/// <param name="Instructions">System instructions prepended to <paramref name="ConversationContext" />.</param>
/// <param name="Tools">Offer-list tools projected from the runtime package before executable registry resolution.</param>
/// <param name="ConversationContext">Prior conversation turns that should seed the agent run.</param>
/// <param name="ReasoningEffort">Optional reasoning budget hint mapped to provider-specific chat options.</param>
/// <param name="SupportsThinking">
///     When <c>true</c> the factory attaches the Ollama-specific <c>think</c> chat option for this turn; when
///     <c>false</c> the option is omitted entirely. The loopback path sets this from the active model's advertised
///     <c>thinking</c> capability so an incapable model never receives the field (Ollama returns HTTP 400 otherwise).
///     Defaults to <c>true</c> so cloud providers (which ignore the unknown <c>think</c> property) keep reasoning.
/// </param>
/// <param name="Sampling">
///     Optional developer-gated per-send sampling overrides. Null (the default) keeps the no-override path
///     byte-identical: the factory sets no extra chat options. When present, the factory applies only the non-null
///     fields as native chat options or Ollama additional properties.
/// </param>
/// <param name="Skills">
///     Optional resolved node skills for MAF progressive disclosure. Empty/null (the default) keeps the no-skills path
///     byte-identical: the factory builds the agent with the existing positional <see cref="IChatClient" /> constructor
///     and attaches no context provider. When non-empty, the factory builds an <c>AgentSkillsProvider</c> from these
///     skills and constructs the agent through the options constructor with that provider attached.
/// </param>
/// <param name="EffectiveContextTokens">
///     The launched effective context window (in tokens) of the resolved local runtime for this turn, when known
///     (AUD4-02). Null (the default) keeps the byte-identical no-override path. When set AND the per-send
///     <see cref="InvocationSamplingOptions.NumCtx" /> is not, the factory writes it as the <c>num_ctx</c> chat option so
///     the inner provider-round budgeter sizes against the real window; a per-send <c>num_ctx</c> still wins.
/// </param>
/// <param name="ResponseJsonSchema">
///     Optional JSON schema this turn's output is CONSTRAINED to. Null (the default) keeps the unconstrained path
///     byte-identical: the factory sets no <see cref="ChatOptions.ResponseFormat" />, so no <c>response_format</c>
///     reaches the wire. When set, the factory maps it through <see cref="ChatResponseFormat.ForJsonSchema" />, which
///     the MEAI OpenAI adapter emits at <c>response_format.json_schema.schema</c> — the only path llama-server reads
///     before compiling it into a GBNF grammar. The CALLER owns keeping the schema free of repetition bounds
///     (<c>minLength</c>/<c>maxLength</c>/<c>pattern</c>/<c>minItems</c>/<c>maxItems</c>), which that grammar rejects.
/// </param>
public sealed record InvocationAgentDefinition(
    string ModelId,
    string Instructions,
    IReadOnlyList<AITool> Tools,
    IReadOnlyList<ChatMessage> ConversationContext,
    string? ReasoningEffort = null,
    bool SupportsThinking = true,
    InvocationSamplingOptions? Sampling = null,
    IReadOnlyList<InvocationSkill>? Skills = null,
    int? EffectiveContextTokens = null,
    JsonElement? ResponseJsonSchema = null);
