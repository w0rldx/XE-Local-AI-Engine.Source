namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Policy;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

public sealed partial class InvocationRunner
{
    // Compiles the loopback OrchestrationSpec into the .AI.Agent OrchestrationAgentDefinition: each participant's
    // model is resolved to a concrete installed model (its pinned profile, else the turn's resolved model), and its
    // projected offer list is bridged into AITools with the SAME switch BuildInvocationTools uses (ApiSide → real
    // bridge over ExecuteApiToolCallAsync; ClientLocal → name-only placeholder the factory swaps for the registry
    // executable). The seed history rides on the workflow input, not per participant.
    private async Task<OrchestrationAgentDefinition> BuildOrchestrationDefinitionAsync(RuntimePackage package,
        OrchestrationSpec spec,
        string resolvedModel,
        int? turnEffectiveContextTokens,
        StreamTransport transport,
        CancellationToken invocationToken)
    {
        var participants = new List<OrchestrationParticipant>(spec.Participants.Count);
        foreach (var participant in spec.Participants)
        {
            var participantResolution = await ResolveModelAsync(participant.ModelId ?? resolvedModel, invocationToken).ConfigureAwait(false);
            if (participantResolution.Substituted)
            {
                await transport.EmitNoticeAsync(TurnNoticeKind.ModelSubstituted,
                    BuildModelSubstitutedNoticeMessage(participantResolution.RequestedModel, participantResolution.Model),
                    participant.Key).ConfigureAwait(false);
            }

            // ORC-07: resolve THIS participant's launched effective context window so its inner provider-round budgeter
            // sizes against it, not the shared configured default.
            var participantContextTokens = await ResolveParticipantContextTokensAsync(participantResolution.Model,
                resolvedModel,
                turnEffectiveContextTokens,
                package.InvocationId,
                invocationToken).ConfigureAwait(false);

            participants.Add(new OrchestrationParticipant
            {
                Key = participant.Key,
                Name = participant.Name,
                Description = participant.Description,
                Instructions = participant.Instructions,
                ModelId = participantResolution.Model,
                ReasoningEffort = participant.ReasoningEffort,
                // This participant's OWN effective-model thinking capability, resolved per participant by the
                // orchestration resolver (OrchestrationResolver) rather than the turn model's capability copied to all —
                // so a participant pinned to a non-thinking model can never have a graded effort reach the think wire,
                // and one pinned to a thinking model keeps its reasoning even when the turn model cannot think.
                SupportsThinking = participant.SupportsThinking,
                // Resolved per participant alongside SupportsThinking, for the same reason: a participant pinned to a
                // model whose template renders no reasoning end marker must not be handed a budget llama.cpp will
                // silently ignore, while one pinned to an enforcing model keeps its cap.
                ReasoningBudgetEnforceable = participant.ReasoningBudgetEnforceable,
                EffectiveContextTokens = participantContextTokens,
                Tools = BuildParticipantTools(package, participant.Tools)
            });
        }

        var triage = participants.FirstOrDefault(p => string.Equals(p.Key, spec.TriageParticipantKey, StringComparison.Ordinal))
                     ?? throw new InvalidOperationException("Orchestration spec triage participant is not present in the participant set.");

        var edges = spec.Edges
                        .Select(static edge => new OrchestrationEdge
                        {
                            FromKey = edge.FromKey,
                            ToKey = edge.ToKey,
                            Reason = edge.Reason
                        })
                        .ToArray();

        return new OrchestrationAgentDefinition
        {
            Triage = triage,
            Participants = participants,
            Edges = edges,
            EmitStreamingUpdates = true,
            MaxTurnsPerAgent = spec.MaxTurnsPerAgent,
            ReturnToPrevious = spec.ReturnToPrevious
        };
    }

    /// <summary>
    ///     ORC-07: resolves the launched effective context window (in tokens) for one orchestration participant so its
    ///     inner provider-round budgeter sizes against it. Precedence:
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 A participant on the SAME model as the turn reuses the turn's already-read-back effective window
    ///                 (<see cref="LocalRuntimeWarmer.ResolveEffectiveContextTokensAsync" /> ran once in <see cref="LocalRuntimeWarmer.PrepareLocalRuntimeAsync" />)
    ///                 — no extra probe.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 A participant on a DIFFERENT model reads its window only when a llama.cpp server is ALREADY
    ///                 resident for it (<c>GetRuntimeInfo</c> is a pure in-memory read that returns <see langword="null" />
    ///                 when the model is not running — it never triggers a load).
    ///             </description>
    ///         </item>
    ///     </list>
    ///     Otherwise <see langword="null" />: participant models are deliberately NOT pre-warmed here (warming every
    ///     participant up front is out of scope — VRAM pressure + latency), so a not-yet-resident participant on a
    ///     distinct model keeps the inner budgeter on its configured default window until that participant is launched.
    /// </summary>
    private async Task<int?> ResolveParticipantContextTokensAsync(string participantModel,
        string resolvedModel,
        int? turnEffectiveContextTokens,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(participantModel, resolvedModel, StringComparison.OrdinalIgnoreCase))
        {
            return turnEffectiveContextTokens;
        }

        var provider = await _localRuntimeWarmer.ResolveWarmableProviderAsync(participantModel, invocationId, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return null;
        }

        return await _localRuntimeWarmer.ResolveEffectiveContextTokensAsync(provider, participantModel, invocationId, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<AITool> BuildParticipantTools(RuntimePackage package, IReadOnlyList<AllowedToolDto> tools)
    {
        return
        [
            .. tools.Select(tool => tool.Location switch
            {
                ToolLocation.ApiSide => InvocationToolBridge.Create(tool.Name,
                    tool.Description,
                    tool.ParameterSchema,
                    (arguments, cancellationToken) => _apiToolCallBridge.ExecuteApiToolCallAsync(package.InvocationId, tool.Name, arguments, tool.RequiresApproval, cancellationToken)),
                ToolLocation.ClientLocal => InvocationToolBridge.CreateOfferPlaceholder(tool.Name, tool.RequiresApproval),
                _ => throw new InvalidOperationException($"Unsupported tool location: {tool.Location}")
            })
        ];
    }

    private static ToolApprovalRequestContent ToApprovalRequest(OrchestrationUpdate update)
    {
        // The orchestration session correlates the decision by its own RequestId; the bridged transport only needs a
        // human-readable description, so synthesize a minimal request carrying the tool name awaiting approval.
        var callId = update.RequestId ?? Guid.NewGuid().ToString("N");
        return new ToolApprovalRequestContent(callId, new FunctionCallContent(callId, ApprovalToolName(update)));
    }

    private static string ApprovalToolName(OrchestrationUpdate update)
    {
        return string.IsNullOrWhiteSpace(update.ToolName) ? "tool" : update.ToolName;
    }

    /// <summary>
    ///     The outcome of <see cref="ResolveModelAsync" />: the model that will actually serve the turn, and (when it
    ///     differs from what was requested) the original request so the caller can surface a model-substitution
    ///     notice once the transport exists.
    /// </summary>
    private readonly record struct ModelResolution(string Model, bool Substituted, string? RequestedModel);

    private async Task<ModelResolution> ResolveModelAsync(string? requestedModel, CancellationToken cancellationToken)
    {
        var trimmedModel = requestedModel?.Trim();

        // The "preflight" (runtime reachable + model installed) and its fallback-to-default are Ollama-specific:
        // VerifyOllamaAndModelAsync probes the Ollama daemon and matches against Ollama's installed list. A model
        // served by llama.cpp (a GGUF, e.g. the first-run-provisioned bartowski/...:Q4_K_M) never appears there, so
        // running it would always "fail" and wrongly fall back to the Ollama default model — which the llama.cpp
        // supervisor can't serve ("model is not installed"). Route the preflight by the model's resolved provider:
        // for any non-Ollama provider, trust the provider/supervisor to validate installation and cold-start the
        // process downstream (LlamaServerProcessSupervisor throws a clean NonRetryable if the GGUF is missing).
        if (!string.IsNullOrWhiteSpace(trimmedModel))
        {
            var providerName = await _providerResolver.ResolveProviderNameForModelAsync(trimmedModel, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(providerName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelResolution(trimmedModel, Substituted: false, RequestedModel: trimmedModel);
            }
        }

        if (await _capabilityReporter.VerifyOllamaAndModelAsync(trimmedModel, cancellationToken).ConfigureAwait(false))
        {
            var verifiedModel = string.IsNullOrWhiteSpace(trimmedModel) ? _defaultModel : trimmedModel;
            return new ModelResolution(verifiedModel, Substituted: false, RequestedModel: trimmedModel);
        }

        if (string.IsNullOrWhiteSpace(trimmedModel))
        {
            throw new InvalidOperationException("Ollama is unavailable or the default model is not installed.");
        }

        _logger.LogWarning("Requested model '{RequestedModel}' could not be verified. Falling back to '{FallbackModel}'.",
            trimmedModel,
            _defaultModel);

        return new ModelResolution(_defaultModel, Substituted: true, RequestedModel: trimmedModel);
    }

    /// <summary>Sanitized, user-facing text for a <see cref="TurnNoticeKind.ModelSubstituted" /> notice.</summary>
    private static string BuildModelSubstitutedNoticeMessage(string? requestedModel, string fallbackModel)
    {
        return string.IsNullOrWhiteSpace(requestedModel)
            ? $"The requested model could not be verified; this turn ran on the node's default model '{fallbackModel}' instead."
            : $"Model '{requestedModel}' could not be verified; this turn ran on the node's default model '{fallbackModel}' instead.";
    }

    /// <summary>
    ///     Sanitized, user-facing text for a <see cref="TurnNoticeKind.EffortDispatched" /> notice. Names the tier, the
    ///     concrete effort it resolved to, and the model only when it was actually replaced. Carries no signal value:
    ///     WHY the tier was chosen rides the notice's reason-code detail, which names a rule rather than a measurement.
    /// </summary>
    private static string BuildEffortDispatchedNoticeMessage(ReasoningTier tier, string effort, string model, bool swapped)
    {
        var resolved = $"Reasoning effort 'auto' resolved to {tier} ({effort}) for this turn.";

        return swapped ? resolved + $" This turn ran on '{model}'." : resolved;
    }

    /// <summary>
    ///     Maps the runtime package onto the inputs the reasoning-effort dispatcher may read. Every field has one
    ///     named source here, so nothing is invented at the seam — and nothing that is an IMMUTABLE constraint
    ///     (approval policy, egress gates, path guards, the sandbox, tool authorisation) can be reached from it.
    /// </summary>
    private static ReasoningDispatchRequest BuildDispatchRequest(RuntimePackage package, string resolvedModel)
    {
        // ONE guarded lookup shared by the text and the attachment flag, so the two can never disagree and an empty or
        // assistant-only context cannot throw: both degrade to "no text, no attachments", which scores Normal.
        var latestUser = package.ConversationContext
                                .OrderByDescending(static message => message.SortOrder)
                                .FirstOrDefault(static message => message.Role == MessageRole.User);

        return new ReasoningDispatchRequest(resolvedModel,
            package.SupportsThinking,
            package.ReasoningBudgetEnforceable,
            package.AllowAutoModelSwap,
            package.OrchestrationSpec is not null,
            package.ConversationContext.Count,
            latestUser?.Content ?? string.Empty,
            latestUser?.Images is { Count: > 0 },
            package.AllowedTools.Count,
            package.Skills is { Count: > 0 },
            package.ResponseJsonSchema is not null,
            package.IsUnattended);
    }

    /// <summary>
    ///     Sanitized, user-facing text for a <see cref="TurnNoticeKind.ToolsFiltered" /> notice. Counts only — it never
    ///     names a tool, and it names the escape hatch so a reader knows nothing was taken away.
    /// </summary>
    private static string BuildToolsFilteredNoticeMessage(int hiddenCount, int totalCount)
    {
        return $"{hiddenCount} of {totalCount} tools were held back from this turn to save context; the assistant can list and use them by calling list_tools.";
    }

    /// <summary>Sanitized, user-facing text for a <see cref="TurnNoticeKind.ToolDisabled" /> notice.</summary>
    private static string BuildToolDisabledNoticeMessage(string toolName)
    {
        return $"Tool '{toolName}' was disabled for the rest of this turn after repeated invalid-argument calls.";
    }

    /// <summary>
    ///     True when a tool's <see cref="Microsoft.Extensions.AI.FunctionResultContent.Result" /> is the structured
    ///     <c>{"error":"tool_disabled",...}</c> marker <c>ToolArgumentRepairResult.ToolDisabled</c> (AI.Agent) returns
    ///     instead of throwing once a tool is cut off after repeated invalid-argument calls. Parses defensively (a
    ///     normal tool result is rarely JSON and never fails this check) rather than substring-matching the JSON text.
    /// </summary>
    private static bool IsToolDisabledResult(string? result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(result);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var errorProperty)
                   && errorProperty.ValueKind == JsonValueKind.String
                   && string.Equals(errorProperty.GetString(), "tool_disabled", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private InvocationAgentDefinition BuildInvocationDefinition(RuntimePackage package,
        string resolvedModel,
        IReadOnlyList<ChatMessage> messages,
        int? effectiveContextTokens)
    {
        return new InvocationAgentDefinition(resolvedModel,
            package.ResolvedSystemPrompt,
            BuildInvocationTools(package),
            messages,
            package.ReasoningEffort,
            package.SupportsThinking,
            MapSamplingOptions(package.SamplingOptions),
            MapSkills(package.Skills),
            effectiveContextTokens,
            package.ResponseJsonSchema,
            package.ReasoningBudgetEnforceable);
    }

    /// <summary>
    ///     Applies the conversation-context budget (using the turn's already-resolved <see cref="TurnPolicy.ContextCapacityTokens" />/
    ///     <see cref="TurnPolicy.ReservedOutputTokens" />, kept in lockstep with the factory's num_ctx / output-clamp
    ///     source) to a message list. The FIRST time a trim occurs in an invocation this logs once (unchanged) AND
    ///     emits a single sanitized <see cref="TurnNoticeKind.HistoryTruncated" /> chat notice carrying counts only —
    ///     never content. When the budgeter still reports <c>ExceedsBudget</c> after its two-pass truncation, this is a
    ///     HARD STOP: it throws <see cref="ContextBudgetExceededException" /> (a classified, pre-inference failure —
    ///     see <see cref="InvocationFailureClassifier.MapFailure" />) instead of proceeding with an over-budget send. Returns the input unchanged
    ///     (reference-equal) when nothing was trimmed.
    ///     <para>
    ///         <paramref name="toolBudgetDefinitions" /> is built ONCE per turn by the caller rather than here: the
    ///         budgeter measures each definition as a framed message and memoizes that framing by string instance, so
    ///         rebuilding the (identical) strings on every call would both re-concatenate them and guarantee a memo miss
    ///         that re-scans every tool schema.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> ApplyContextBudgetAsync(IReadOnlyList<ChatMessage> messages,
        RuntimePackage package,
        IReadOnlyList<string> toolBudgetDefinitions,
        string resolvedModel,
        string stage,
        TurnPolicy turnPolicy,
        StreamTransport transport,
        ContextBudgetNoticeGate gate)
    {
        // ORC-02: the resolved system prompt is prepended to the request AFTER this history (BuildInvocationDefinition),
        // and tool JSON schemas are never in the message list — so feed both to the budgeter as fixed overhead. It folds
        // them into the effective budget, mirroring the inner ProviderCallBudgetChatClient, so the outer budget and its
        // hard-stop measure the true round rather than history alone.
        var result = _contextBudgeter.Budget(messages,
            turnPolicy.ContextCapacityTokens,
            turnPolicy.ReservedOutputTokens,
            package.ResolvedSystemPrompt,
            toolBudgetDefinitions,
            resolvedModel);

        if (!result.Trimmed && !result.ExceedsBudget)
        {
            return result.Messages;
        }

        if (!gate.Logged)
        {
            gate.Logged = true;
            _logger.LogWarning(
                "Conversation context budgeted for invocation {InvocationId} ({Stage}): dropped {Dropped} message(s), truncated {Truncated} tool result(s) ({Chars} chars), stripped reasoning from {ReasoningStripped} message(s), excerpted {ProtectedResultsExcerpted} protected tool result(s), estimated tokens {Before} -> {After}, capacity {Capacity} reserving {Reserved} (still over budget: {Overflow}).",
                package.InvocationId,
                stage,
                result.MessagesDropped,
                result.ToolResultsTruncated,
                result.CharsTruncated,
                result.ReasoningStrippedCount,
                result.ProtectedResultsExcerptedCount,
                result.EstimatedTokensBefore,
                result.EstimatedTokensAfter,
                turnPolicy.ContextCapacityTokens,
                turnPolicy.ReservedOutputTokens,
                result.ExceedsBudget);
        }

        if (result.ExceedsBudget)
        {
            throw new ContextBudgetExceededException(ContextBudgetExceededMessage);
        }

        if (!gate.NoticeEmitted)
        {
            gate.NoticeEmitted = true;
            await transport.EmitNoticeAsync(TurnNoticeKind.HistoryTruncated,
                BuildHistoryTruncatedNoticeMessage(result)).ConfigureAwait(false);
        }

        return result.Messages;
    }

    /// <summary>
    ///     Sanitized, user-facing text for a <see cref="TurnNoticeKind.HistoryTruncated" /> notice — counts only, never
    ///     content. The two last-resort budgeter passes are reported DISTINCTLY (reasoning removed vs. recent tool output
    ///     shortened) because they mean different things to a reader: one discards the model's scratch-pad, the other
    ///     shortens output the current round is working with.
    ///     <para>
    ///         The reassurance that "the originals are kept" is deliberately withheld once either fires. It is true of the
    ///         saved conversation — which this never edits — but NOT of what those passes reclaim: reasoning and tool
    ///         output produced inside this turn's tool/approval loop are never persisted, so once dropped they are gone
    ///         rather than merely absent from this send. Saying otherwise would be a comfortable lie.
    ///     </para>
    /// </summary>
    private static string BuildHistoryTruncatedNoticeMessage(ConversationBudgetResult result)
    {
        var builder = new StringBuilder("Conversation history was trimmed to fit the model's context window (")
                      .Append(result.MessagesDropped)
                      .Append(" older message(s) dropped, ")
                      .Append(result.ToolResultsTruncated)
                      .Append(" tool result(s) shortened");

        if (result.ReasoningStrippedCount > 0)
        {
            _ = builder.Append(", reasoning removed from ").Append(result.ReasoningStrippedCount).Append(" message(s)");
        }

        if (result.ProtectedResultsExcerptedCount > 0)
        {
            _ = builder.Append(", ").Append(result.ProtectedResultsExcerptedCount).Append(" recent tool result(s) shortened");
        }

        _ = builder.Append("). ");

        _ = result.ReasoningStrippedCount > 0 || result.ProtectedResultsExcerptedCount > 0
            ? builder.Append(
                "Your saved messages are unchanged, but the reasoning and tool output reclaimed from this turn are not kept — use Compact to summarize older messages and preserve their context.")
            : builder.Append("The originals are kept — use Compact to summarize older messages and preserve their context.");

        return builder.ToString();
    }

    /// <summary>
    ///     Mutable "logged/notified once" gate threaded through the (possibly several) <see cref="ApplyContextBudgetAsync" />
    ///     calls of one invocation. A plain class (not a <c>ref bool</c>) because <c>ref</c> locals cannot cross an
    ///     <c>await</c>/be captured by an async method.
    /// </summary>
    private sealed class ContextBudgetNoticeGate
    {
        public bool Logged { get; set; }

        public bool NoticeEmitted { get; set; }
    }

    /// <summary>
    ///     Maps the resolved client-side <see cref="ResolvedSkill" /> set onto the provider-agnostic
    ///     <see cref="InvocationSkill" /> records the factory builds into a MAF <c>AgentSkillsProvider</c> (.AI.Agent
    ///     cannot reference Client.Models). Returns null for a null/empty set so the no-skills path stays byte-identical
    ///     (the factory keeps the existing positional constructor and attaches no context provider). The two records are
    ///     deliberate duplicates rather than one shared type: the layer test freezes .AI.Agent as unable to reference
    ///     Client.*, and a field-for-field copy is cheaper than the project it would otherwise take to share them.
    ///     <para>
    ///         This is a pure rename of fields — no trust decision is taken or reversed here. An imported skill's body
    ///         and resource payloads were already fenced by the resolver, so what crosses this boundary is what reaches
    ///         the model.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<InvocationSkill>? MapSkills(IReadOnlyList<ResolvedSkill>? skills)
    {
        if (skills is not { Count: > 0 })
        {
            return null;
        }

        return
        [
            .. skills.Select(static skill => new InvocationSkill(skill.Name,
                skill.Description,
                skill.Body,
                skill.License,
                skill.Compatibility,
                skill.AllowedTools,
                skill.Metadata,
                MapSkillResources(skill.Resources)))
        ];
    }

    /// <summary>
    ///     Maps one skill's bundled resources onto their provider-agnostic mirror. Null for a skill with no resources,
    ///     so the instructions-only skill builds exactly the <c>AgentInlineSkill</c> it built before resources existed.
    /// </summary>
    private static IReadOnlyList<InvocationSkillResource>? MapSkillResources(IReadOnlyList<ResolvedSkillResource>? resources)
    {
        if (resources is not { Count: > 0 })
        {
            return null;
        }

        return [.. resources.Select(static resource => new InvocationSkillResource(resource.Name, resource.Description, resource.MediaType, resource.Content))];
    }

    /// <summary>
    ///     Maps the client-side <see cref="SamplingOptions" /> onto the provider-agnostic
    ///     <see cref="InvocationSamplingOptions" /> the factory consumes (.AI.Agent cannot reference Client.Models).
    ///     Returns null when no overrides were requested so the no-override path stays byte-identical.
    /// </summary>
    private static InvocationSamplingOptions? MapSamplingOptions(SamplingOptions? sampling)
    {
        if (sampling is null)
        {
            return null;
        }

        return new InvocationSamplingOptions
        {
            Temperature = sampling.Temperature,
            TopP = sampling.TopP,
            TopK = sampling.TopK,
            MinP = sampling.MinP,
            MaxOutputTokens = sampling.MaxOutputTokens,
            ReasoningBudgetTokens = sampling.ReasoningBudgetTokens,
            RepeatPenalty = sampling.RepeatPenalty,
            RepeatLastN = sampling.RepeatLastN,
            PresencePenalty = sampling.PresencePenalty,
            FrequencyPenalty = sampling.FrequencyPenalty,
            // The wire seed is a string (precision-safe). It is validated at the send boundary, so parse leniently here:
            // an unparseable value maps to no override rather than throwing on the invocation hot path.
            Seed = SeedValue.TryParse(sampling.Seed, out var seed, out _) ? seed : null,
            Stop = sampling.Stop,
            NumCtx = sampling.NumCtx
        };
    }

    /// <summary>
    ///     Renders the package's conversation context as the provider-bound message list. Internal rather than private
    ///     for the unit tests that pin the tool-history replay shape (the assembly already grants
    ///     <c>InternalsVisibleTo</c> to the test project); not part of the public contract.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> BuildChatMessages(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var messages = new List<ChatMessage>(package.ConversationContext.Count);
        foreach (var message in package.ConversationContext.OrderBy(static message => message.SortOrder))
        {
            // Replayed tool history first, so the model reads call -> result -> the turn's own text in the order it
            // happened. Only the integration coordinator attaches these, and only for a caller-managed session.
            if (message.ToolExchanges is { Count: > 0 } exchanges)
            {
                ConversationToolExchangeMessages.Append(messages, exchanges);
            }

            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(message.Thinking))
            {
                contents.Add(new TextReasoningContent(message.Thinking));
            }

            // The blank text part is dropped ONLY for a turn carrying replayed exchanges: Microsoft.Extensions.AI's
            // OpenAI client takes its tool-calls-only branch only when the message has no content part, and an empty
            // TextContent alongside tool_calls is content some chat templates reject rather than ignore. Every other
            // turn keeps it, so an image-only vision turn still goes out as [TextContent(""), DataContent] exactly as
            // it always has.
            if (!string.IsNullOrEmpty(message.Content) || message.ToolExchanges is not { Count: > 0 })
            {
                contents.Add(new TextContent(message.Content));
            }

            // Vision (multimodal) parts: the turn assembler attaches these only for a vision-capable
            // effective model, so an image never reaches a model that cannot see it.
            if (message.Images is { Count: > 0 } images)
            {
                foreach (var image in images)
                {
                    contents.Add(new DataContent(image.Data, image.MediaType));
                }
            }

            if (contents.Count > 0)
            {
                messages.Add(new ChatMessage(MapRole(message.Role), contents));
            }
        }

        return messages;
    }

    /// <summary>
    ///     ORC-02: renders each offered tool's model-facing definition (name + description + parameter schema) as one
    ///     text unit for the outer context budgeter's fixed-overhead estimate. Reads the raw <see cref="AllowedToolDto" />
    ///     schema string — present for BOTH Api-side and client-local tools — rather than the built bridge, whose
    ///     client-local offer placeholders carry no schema until the factory swaps them, so the schema footprint is
    ///     counted for every tool. Returns an empty list when the package offers no tools.
    /// </summary>
    private static IReadOnlyList<string> BuildToolBudgetDefinitions(RuntimePackage package)
    {
        if (package.AllowedTools.Count == 0)
        {
            return [];
        }

        return [.. package.AllowedTools.Select(static tool => string.Concat(tool.Name, "\n", tool.Description, "\n", tool.ParameterSchema))];
    }

    private IReadOnlyList<AITool> BuildInvocationTools(RuntimePackage package)
    {
        // The runtime package only carries the OFFER list. Api-side tools get a real bridge that round-trips to the
        // platform; client-local (catalog) tools get a name-only placeholder, and the invocation factory swaps it for
        // the matching executable from IAgentToolRegistry before the agent runs.
        return
        [
            .. package.AllowedTools.Select(tool => tool.Location switch
            {
                ToolLocation.ApiSide => InvocationToolBridge.Create(tool.Name,
                    tool.Description,
                    tool.ParameterSchema,
                    (arguments, cancellationToken) => _apiToolCallBridge.ExecuteApiToolCallAsync(package.InvocationId, tool.Name, arguments, tool.RequiresApproval, cancellationToken)),
                ToolLocation.ClientLocal => InvocationToolBridge.CreateOfferPlaceholder(tool.Name, tool.RequiresApproval),
                _ => throw new InvalidOperationException($"Unsupported tool location: {tool.Location}")
            })
        ];
    }

    private static ChatRole MapRole(MessageRole role)
    {
        return role switch
        {
            MessageRole.System => ChatRole.System,
            MessageRole.User => ChatRole.User,
            MessageRole.Assistant => ChatRole.Assistant,
            MessageRole.Tool => ChatRole.Tool,
            _ => throw new InvalidOperationException($"Unsupported message role: {role}")
        };
    }
}
