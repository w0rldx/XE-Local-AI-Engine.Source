namespace XE_Local_AI_Engine.AI.Agent.Invocation;

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
public sealed record InvocationAgentDefinition(
    string ModelId,
    string Instructions,
    IReadOnlyList<AITool> Tools,
    IReadOnlyList<ChatMessage> ConversationContext,
    string? ReasoningEffort = null,
    bool SupportsThinking = true,
    InvocationSamplingOptions? Sampling = null);
