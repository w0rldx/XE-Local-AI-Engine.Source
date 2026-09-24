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
    // Compiles the loopback OrchestrationSpec into an OrchestrationAgentDefinition: each participant's model resolves
    // to its pin or the turn's, and its offer bridges through the SAME switch BuildInvocationTools uses.
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
            var participantResolution = await ResolveModelAsync(participant.ModelId ?? resolvedModel, invocationToken);
            if (participantResolution.Substituted)
            {
                await transport.EmitNoticeAsync(TurnNoticeKind.ModelSubstituted,
                    BuildModelSubstitutedNoticeMessage(participantResolution.RequestedModel, participantResolution.Model),
                    participant.Key);
            }

            // Resolve THIS participant's launched effective context window so its inner provider-round budgeter
            // sizes against it, not the shared configured default.
            var participantContextTokens = await ResolveParticipantContextTokensAsync(participantResolution.Model,
                resolvedModel,
                turnEffectiveContextTokens,
                package.InvocationId,
                invocationToken);

            participants.Add(new OrchestrationParticipant
            {
                Key = participant.Key,
                Name = participant.Name,
                Description = participant.Description,
                Instructions = participant.Instructions,
                ModelId = participantResolution.Model,
                ReasoningEffort = participant.ReasoningEffort,
                // This participant's OWN capability, resolved per participant rather than copied from the turn model:
                // a graded effort must never reach a non-thinking pin, nor a thinking pin lose its reasoning.
                SupportsThinking = participant.SupportsThinking,
                // Per participant alongside SupportsThinking: a model whose template renders no reasoning end marker
                // must not be handed a budget llama.cpp silently ignores, while an enforcing pin keeps its cap.
                ReasoningBudgetEnforceable = participant.ReasoningBudgetEnforceable,
                EffectiveContextTokens = participantContextTokens,
                Tools = BuildParticipantTools(participant.Tools)
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
    ///     Resolves the launched effective context window for one orchestration participant, so its inner
    ///     provider-round budgeter sizes against it.
    /// </summary>
    /// <remarks>
    ///     A participant on the turn's own model reuses the window
    ///     <see cref="LocalRuntimeWarmer.ResolveEffectiveContextTokensAsync" /> already read back for it. One on a DIFFERENT model reads
    ///     its window only when a llama.cpp server is ALREADY resident, <c>GetRuntimeInfo</c> being an in-memory read
    ///     that never triggers a load. Otherwise <see langword="null" />: participants are deliberately not pre-warmed
    ///     (VRAM and latency), so the inner budgeter keeps its default window until that participant launches.
    /// </remarks>
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

        var provider = await _localRuntimeWarmer.ResolveWarmableProviderAsync(participantModel, invocationId, cancellationToken);
        if (provider is null)
        {
            return null;
        }

        return await _localRuntimeWarmer.ResolveEffectiveContextTokensAsync(provider, participantModel, invocationId, cancellationToken);
    }

    private static IReadOnlyList<AITool> BuildParticipantTools(IReadOnlyList<AllowedToolDto> tools)
    {
        return
        [
            .. tools.Select(tool => tool.Location switch
            {
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

        // The preflight and its fallback-to-default are Ollama-specific: a GGUF never appears in Ollama's installed
        // list, so it would always "fail" into an Ollama default. Route by provider; the supervisor validates its own.
        if (!string.IsNullOrWhiteSpace(trimmedModel))
        {
            var providerName = await _providerResolver.ResolveProviderNameForModelAsync(trimmedModel, cancellationToken);
            if (!string.Equals(providerName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelResolution(trimmedModel, Substituted: false, RequestedModel: trimmedModel);
            }
        }

        if (await _capabilityReporter.VerifyOllamaAndModelAsync(trimmedModel, cancellationToken))
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
    ///     Sanitized, user-facing text for a <see cref="TurnNoticeKind.EffortDispatched" /> notice: the tier, the
    ///     effort it resolved to, and the model only when it was actually replaced.
    /// </summary>
    /// <remarks>
    ///     It carries no signal value. WHY the tier was chosen rides the notice's reason-code detail, which names a
    ///     rule rather than a measurement.
    /// </remarks>
    private static string BuildEffortDispatchedNoticeMessage(ReasoningTier tier, string effort, string model, bool swapped)
    {
        var resolved = $"Reasoning effort 'auto' resolved to {tier} ({effort}) for this turn.";

        return swapped ? resolved + $" This turn ran on '{model}'." : resolved;
    }

    /// <summary>Maps the runtime package onto the inputs the reasoning-effort dispatcher may read.</summary>
    /// <remarks>
    ///     Every field has one named source here, so nothing is invented at the seam, and no IMMUTABLE constraint —
    ///     approval policy, egress gates, path guards, the sandbox, tool authorisation — is reachable from it.
    /// </remarks>
    private static ReasoningDispatchRequest BuildDispatchRequest(RuntimePackage package, string resolvedModel)
    {
        // ONE guarded lookup shared by the text and the attachment flag, so the two can never disagree and an empty or
        // assistant-only context cannot throw: both degrade to "no text, no attachments", which scores Normal.
        var latestUser = package.ConversationContext
                                .OrderByDescending(static message => message.SortOrder)
                                .FirstOrDefault(static message => message.Role == MessageRole.User);

        return new ReasoningDispatchRequest
        {
            ResolvedModel = resolvedModel,
            SupportsThinking = package.SupportsThinking,
            ReasoningBudgetEnforceable = package.ReasoningBudgetEnforceable,
            AllowAutoModelSwap = package.AllowAutoModelSwap,
            HasOrchestration = package.OrchestrationSpec is not null,
            ConversationDepth = package.ConversationContext.Count,
            LatestUserText = latestUser?.Content ?? string.Empty,
            HasAttachments = latestUser?.Images is { Count: > 0 },
            OfferedToolCount = package.AllowedTools.Count,
            HasSkills = package.Skills is { Count: > 0 },
            HasResponseSchema = package.ResponseJsonSchema is not null,
            IsUnattended = package.IsUnattended
        };
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
    ///     marker <c>ToolArgumentRepairResult.ToolDisabled</c> returns once a tool is cut off.
    /// </summary>
    /// <remarks>
    ///     The marker is returned instead of throwing after repeated invalid-argument calls. Parsed defensively — a
    ///     normal tool result is rarely JSON and never fails this check — rather than substring-matched.
    /// </remarks>
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

    private static InvocationAgentDefinition BuildInvocationDefinition(RuntimePackage package,
        string resolvedModel,
        IReadOnlyList<ChatMessage> messages,
        int? effectiveContextTokens)
    {
        return new InvocationAgentDefinition
        {
            ModelId = resolvedModel,
            Instructions = package.ResolvedSystemPrompt,
            OmitSystemPrompt = package.OmitSystemPrompt,
            Tools = BuildInvocationTools(package),
            ConversationContext = messages,
            ReasoningEffort = package.ReasoningEffort,
            SupportsThinking = package.SupportsThinking,
            Sampling = MapSamplingOptions(package.SamplingOptions),
            Skills = MapSkills(package.Skills),
            EffectiveContextTokens = effectiveContextTokens,
            ResponseJsonSchema = package.ResponseJsonSchema,
            ReasoningBudgetEnforceable = package.ReasoningBudgetEnforceable
        };
    }

    /// <summary>
    ///     Applies the turn's already-resolved <see cref="TurnPolicy.ContextCapacityTokens" /> and
    ///     <see cref="TurnPolicy.ReservedOutputTokens" /> to a message list, reference-equal when nothing was trimmed.
    /// </summary>
    /// <remarks>
    ///     The budget stays in lockstep with the factory's num_ctx and output-clamp source. The first trim of an
    ///     invocation logs once and emits one sanitized <see cref="TurnNoticeKind.HistoryTruncated" /> notice carrying
    ///     counts, never content. A budgeter still reporting <c>ExceedsBudget</c> after its two passes is a HARD STOP:
    ///     <see cref="ContextBudgetExceededException" />, classified by <see cref="InvocationFailureClassifier.MapFailure" />.
    ///     <paramref name="toolBudgetDefinitions" /> is built once per turn: the budgeter memoizes framing by instance.
    /// </remarks>
    private async Task<IReadOnlyList<ChatMessage>> ApplyContextBudgetAsync(IReadOnlyList<ChatMessage> messages,
        RuntimePackage package,
        IReadOnlyList<string> toolBudgetDefinitions,
        string resolvedModel,
        string stage,
        TurnPolicy turnPolicy,
        StreamTransport transport,
        ContextBudgetNoticeGate gate)
    {
        // The system prompt (blank when omitted) and tool schemas sit outside this history. Count both as fixed
        // overhead, matching the inner budgeter so the hard stop measures the actual request.
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
                BuildHistoryTruncatedNoticeMessage(result));
        }

        return result.Messages;
    }

    /// <summary>
    ///     Sanitized, user-facing text for a <see cref="TurnNoticeKind.HistoryTruncated" /> notice: counts only, never
    ///     content.
    /// </summary>
    /// <remarks>
    ///     The two last-resort passes are reported DISTINCTLY because they mean different things — one discards the
    ///     model's scratch-pad, the other shortens output the current round is working with. Once either fires, the
    ///     reassurance that the originals are kept is withheld: it holds for the saved conversation, but reasoning and
    ///     tool output produced inside this turn's loop are never persisted, so once dropped they are gone.
    /// </remarks>
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
    ///     <see cref="InvocationSkill" /> records the factory builds into a MAF <c>AgentSkillsProvider</c>.
    /// </summary>
    /// <remarks>
    ///     Null for a null or empty set, so the no-skills path stays byte-identical. The two records are deliberate
    ///     duplicates: the layer test freezes .AI.Agent as unable to reference <c>Client.*</c>, and a field-for-field
    ///     copy is cheaper than the project sharing them would take. A pure rename of fields — no trust decision is
    ///     taken or reversed, the resolver having already fenced an imported skill's body and resources.
    /// </remarks>
    private static IReadOnlyList<InvocationSkill>? MapSkills(IReadOnlyList<ResolvedSkill>? skills)
    {
        if (skills is not { Count: > 0 })
        {
            return null;
        }

        return
        [
            .. skills.Select(static skill => new InvocationSkill
            {
                Name = skill.Name,
                Description = skill.Description,
                Body = skill.Body,
                License = skill.License,
                Compatibility = skill.Compatibility,
                AllowedTools = skill.AllowedTools,
                Metadata = skill.Metadata,
                Resources = MapSkillResources(skill.Resources)
            })
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

        return
        [
            .. resources.Select(static resource => new InvocationSkillResource
            {
                Name = resource.Name,
                Description = resource.Description,
                MediaType = resource.MediaType,
                Content = resource.Content
            })
        ];
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

    /// <summary>Renders the package's conversation context as the provider-bound message list.</summary>
    /// <remarks>
    ///     Internal rather than private for the unit tests that pin the tool-history replay shape; not part of the
    ///     public contract.
    /// </remarks>
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

            // The blank text part is dropped ONLY for a turn with replayed exchanges: the OpenAI client takes its
            // tool-calls-only branch only with no content part, and some templates reject an empty one beside them.
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
    ///     Renders each offered tool's model-facing definition as one text unit for the outer context budgeter's
    ///     fixed-overhead estimate, or an empty list when the package offers none.
    /// </summary>
    /// <remarks>
    ///     It reads the raw <see cref="AllowedToolDto" /> schema string, present for BOTH Api-side and client-local
    ///     tools, rather than the built bridge, whose client-local placeholders carry no schema until the factory
    ///     swaps them — so the schema footprint is counted for every tool.
    /// </remarks>
    private static IReadOnlyList<string> BuildToolBudgetDefinitions(RuntimePackage package)
    {
        if (package.AllowedTools.Count == 0)
        {
            return [];
        }

        return [.. package.AllowedTools.Select(static tool => string.Concat(tool.Name, "\n", tool.Description, "\n", tool.ParameterSchema))];
    }

    private static IReadOnlyList<AITool> BuildInvocationTools(RuntimePackage package)
    {
        // The package carries only the OFFER list: a client-local tool becomes a name-only placeholder that the
        // invocation factory swaps for the IAgentToolRegistry executable before the agent runs.
        return
        [
            .. package.AllowedTools.Select(tool => tool.Location switch
            {
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
