namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Policy;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
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
        ChatOutputCap outputCap,
        ReasoningBudgets reasoningBudgets,
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
            var participantWindow = await ResolveParticipantContextTokensAsync(participantResolution.Model,
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
                EffectiveContextTokens = participantWindow.ContextTokens,
                ReasoningBudgets = reasoningBudgets,
                // Participants can run different models, so each is capped against ITS window, not the turn's; a local model
                // not loaded yet takes the ceiling, since nothing recomputes the cap after its deferred load.
                DefaultMaxOutputTokens = participantWindow.UnlaunchedLocalModel
                    ? outputCap.TokensForUnlaunchedLocalModel()
                    : outputCap.TokensFor(participantWindow.ContextTokens),
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
    private async Task<ParticipantWindow> ResolveParticipantContextTokensAsync(string participantModel,
        string resolvedModel,
        int? turnEffectiveContextTokens,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(participantModel, resolvedModel, StringComparison.OrdinalIgnoreCase))
        {
            return new ParticipantWindow(turnEffectiveContextTokens, UnlaunchedLocalModel: false);
        }

        var provider = await _localRuntimeWarmer.ResolveWarmableProviderAsync(participantModel, invocationId, cancellationToken);
        if (provider is null)
        {
            return new ParticipantWindow(ContextTokens: null, UnlaunchedLocalModel: false);
        }

        var contextTokens = await _localRuntimeWarmer.ResolveEffectiveContextTokensAsync(provider, participantModel, invocationId, cancellationToken);
        return new ParticipantWindow(contextTokens, UnlaunchedLocalModel: contextTokens is null);
    }

    /// <summary>A participant's launched window, and whether it is unknown because its local llama.cpp model is not loaded yet.</summary>
    private readonly record struct ParticipantWindow(int? ContextTokens, bool UnlaunchedLocalModel);

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
        int? effectiveContextTokens,
        ReasoningBudgets? reasoningBudgets = null,
        int? defaultMaxOutputTokens = null)
    {
        return new InvocationAgentDefinition
        {
            DefaultMaxOutputTokens = defaultMaxOutputTokens,
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
            ReasoningBudgetEnforceable = package.ReasoningBudgetEnforceable,
            ReasoningBudgets = reasoningBudgets
        };
    }

    /// <summary>A package's first provider round, budgeted exactly as the tool-loop stage budgets it before any window is launched.</summary>
    /// <remarks>
    ///     The agent factory's own seed over the rendered conversation, the same tool definitions, and the window and reservation
    ///     <see cref="TurnPolicy.Resolve" /> sets. The benchmark freeze refuses with it, so its pre-flight and the run's hard stop cannot
    ///     drift; the launched window can only be smaller, so a pass there is necessary, not sufficient.
    /// </remarks>
    internal static ConversationBudgetResult BudgetFirstRound(IConversationContextBudgeter budgeter,
        RuntimePackage package,
        ConversationContextBudgetOptions budgetOptions,
        int defaultContextTokens,
        string resolvedModel)
    {
        ArgumentNullException.ThrowIfNull(budgeter);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(budgetOptions);
        var (_, capacity, reserved) = TurnPolicy.ResolveContextBudget(package, budgetOptions, defaultContextTokens);
        var seed = InvocationAgentFactory.BuildSeedMessages(BuildInvocationDefinition(package, resolvedModel, BuildChatMessages(package), effectiveContextTokens: null));
        return budgeter.Budget(seed, capacity, reserved, package.ResolvedSystemPrompt, BuildToolBudgetDefinitions(package.AllowedTools), resolvedModel);
    }

    /// <summary>The first round fitted to the launched window: the package, the tool definitions the budgeter counts, and what was narrowed.</summary>
    /// <remarks><see cref="Measured" /> is the budget of <see cref="Messages" />, so the initial assembly does not measure the same request twice.</remarks>
    private readonly record struct FirstRoundFit(RuntimePackage Package,
        IReadOnlyList<ChatMessage> Messages,
        IReadOnlyList<string> ToolDefinitions,
        ConversationBudgetResult Measured,
        FittedToolOffer? FittedOffer,
        bool AttachmentShortened);

    /// <summary>
    ///     Fits the two fixed costs a first message cannot compact away to the launched window: the tool offer (model-matrix
    ///     F5) and the inlined attachment (F6).
    /// </summary>
    /// <remarks>
    ///     The offer is sized without the attachment, which then shrinks to the room the tools leave. A too-large offer is
    ///     narrowed by the relevance hop to a token budget: the pinned tools, then ranked tools in rank order while they fit,
    ///     never below the pinned tools plus one ranked tool, so <c>list_tools</c> can still reveal the rest. The attachment is
    ///     recomposed under a smaller budget, which keeps its fence intact and tells the model it was truncated. Either one
    ///     that cannot fit throws with its own cause.
    /// </remarks>
    private FirstRoundFit FitFirstRoundToWindow(RuntimePackage package, string resolvedModel, TurnPolicy turnPolicy)
    {
        var definitions = BuildToolBudgetDefinitions(package.AllowedTools);
        var messages = BuildChatMessages(package);
        var measured = BudgetMessages(messages, package, definitions, resolvedModel, turnPolicy);
        if (!measured.ExceedsBudget)
        {
            return new FirstRoundFit(package, messages, definitions, measured, FittedOffer: null, AttachmentShortened: false);
        }

        bool Exceeds(RuntimePackage candidate, IReadOnlyList<string> toolDefinitions) =>
            BudgetMessages(BuildChatMessages(candidate), candidate, toolDefinitions, resolvedModel, turnPolicy).ExceedsBudget;

        var attachment = package.ConversationContext.Find(static message => message.ShortenAttachment is not null);
        var withoutAttachment = attachment is null
            ? package
            : package with
            {
                ConversationContext = [.. package.ConversationContext.Where(message => !ReferenceEquals(message, attachment))]
            };
        FittedToolOffer? fittedOffer = null;
        // Only when the turn fits with no tools at all: otherwise the offer is not the cause, and the generic hard stop applies.
        if (definitions.Count > 0 && !package.DisableToolRelevanceFilter && Exceeds(withoutAttachment, definitions) && !Exceeds(withoutAttachment, []))
        {
            // list_tools and ask_user are always offered; every other tool is costed by its own definition. Overhead is additive per
            // definition, so a tool's cost is what it adds to the pinned set's.
            List<string> pinned = [ListToolsFunction.BudgetDefinition];
            var ranked = new List<(string Name, string Definition)>();
            for (var index = 0; index < package.AllowedTools.Count; index++)
            {
                if (string.Equals(package.AllowedTools[index].Name, AskUserTool.ToolName, StringComparison.Ordinal))
                {
                    pinned.Add(definitions[index]);
                }
                else
                {
                    ranked.Add((package.AllowedTools[index].Name, definitions[index]));
                }
            }

            int Overhead(IReadOnlyList<string> toolDefinitions) =>
                _contextBudgeter.Budget([], turnPolicy.ContextCapacityTokens, turnPolicy.ReservedOutputTokens, systemPrompt: null, toolDefinitions, resolvedModel).FixedOverheadTokens;

            var pinnedOverhead = Overhead(pinned);
            var costs = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (name, definition) in ranked)
            {
                costs[name] = Overhead([.. pinned, definition]) - pinnedOverhead;
            }

            // The full offer exceeds after every trim pass ran, so its measured size is the irreducible turn; what the pinned set
            // leaves above it is the room the ranked tools share.
            var irreducible = BudgetMessages(BuildChatMessages(withoutAttachment), withoutAttachment, definitions, resolvedModel, turnPolicy).EstimatedTokensAfter;
            var room = BudgetMessages(BuildChatMessages(withoutAttachment), withoutAttachment, pinned, resolvedModel, turnPolicy).EffectiveBudgetTokens - irreducible;

            // Less one tool call and its result, measured by the same budgeter: filled to the edge, the 4B's second round overflowed (live C2).
            List<ChatMessage> roundTrip =
            [
                new(ChatRole.Assistant,
                    [new FunctionCallContent("call_0", "tool",new Dictionary<string, object?>(StringComparer.Ordinal) { ["input"] =new string('x', FittedRoundTripArgumentCharacters) })]),
                new(ChatRole.Tool, [new FunctionResultContent("call_0", new string('x', FittedRoundTripResultCharacters))])
            ];
            room -= _contextBudgeter.Budget(roundTrip, turnPolicy.ContextCapacityTokens, turnPolicy.ReservedOutputTokens, systemPrompt: null, [], resolvedModel).EstimatedTokensBefore;

            // The floor: the pinned tools plus at least one ranked tool.
            if (costs.Count == 0 || room < costs.Values.Min())
            {
                throw new ContextBudgetExceededException(ToolOfferExceedsWindowMessage);
            }

            fittedOffer = new FittedToolOffer
            {
                RankedTokenBudget = room,
                TokenCosts = costs
            };

            // The later budget stages measure the costliest offer the hop could send in any rank order.
            foreach (var index in CostliestWithin([.. ranked.Select(entry => costs[entry.Name])], room))
            {
                pinned.Add(ranked[index].Definition);
            }

            definitions = pinned;
        }

        var shortened = false;
        messages = BuildChatMessages(package);
        measured = BudgetMessages(messages, package, definitions, resolvedModel, turnPolicy);

        // Only when the turn fits without it: otherwise the attachment is not the cause, and the generic hard stop names the history.
        if (measured.ExceedsBudget && attachment?.ShortenAttachment is { } shorten && !Exceeds(withoutAttachment, definitions))
        {
            var charBudget = attachment.Content.Length;
            // Each pass removes at least a quarter, so a token estimate that keeps drifting cannot loop for long.
            for (; measured.ExceedsBudget; measured = BudgetMessages(messages, package, definitions, resolvedModel, turnPolicy))
            {
                charBudget -= Math.Max(Math.Max((measured.EstimatedTokensAfter - measured.EffectiveBudgetTokens) * 4, charBudget / 4), 1);
                var content = charBudget > 0 ? shorten(charBudget) : null;
                if (content is null)
                {
                    throw new ContextBudgetExceededException(AttachmentExceedsWindowMessage);
                }

                var current = attachment;
                attachment = attachment with
                {
                    Content = content
                };
                package = package with
                {
                    ConversationContext = [.. package.ConversationContext.Select(message => ReferenceEquals(message, current) ? attachment : message)]
                };
                messages = BuildChatMessages(package);
                shortened = true;
            }
        }

        return new FirstRoundFit(package, messages, definitions, measured, fittedOffer, shortened);
    }

    /// <summary>The indices of the subset with the largest total cost within <paramref name="room" /> (0/1 knapsack).</summary>
    /// <remarks>
    ///     The hop offers tools in rank order while they fit, so any subset within the room can go out. Taking the largest
    ///     costs first is not that bound: room 1000 and costs 600, 500, 500 count 600 where the hop can send 1000.
    /// </remarks>
    internal static IReadOnlyList<int> CostliestWithin(IReadOnlyList<int> costs, int room)
    {
        // lastItem[total] is the item that first reached that total; each total's chain uses strictly earlier items.
        var lastItem = new int[Math.Max(room, 0) + 1];
        Array.Fill(lastItem, -1);
        for (var index = 0; index < costs.Count; index++)
        {
            for (var total = room; total >= costs[index] && costs[index] > 0; total--)
            {
                if (lastItem[total] < 0 && (total == costs[index] || lastItem[total - costs[index]] >= 0))
                {
                    lastItem[total] = index;
                }
            }
        }

        var subset = new List<int>();
        for (var total = Array.FindLastIndex(lastItem, static index => index >= 0); total > 0; total -= costs[lastItem[total]])
        {
            subset.Add(lastItem[total]);
        }

        return subset;
    }

    private ConversationBudgetResult BudgetMessages(IReadOnlyList<ChatMessage> messages,
        RuntimePackage package,
        IReadOnlyList<string> toolDefinitions,
        string resolvedModel,
        TurnPolicy turnPolicy) =>
        _contextBudgeter.Budget(messages, turnPolicy.ContextCapacityTokens, turnPolicy.ReservedOutputTokens, package.ResolvedSystemPrompt, toolDefinitions, resolvedModel);

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
        ContextBudgetNoticeGate gate,
        ConversationBudgetResult? measured = null)
    {
        // Tool schemas sit outside every stage's history; the system prompt (blank when omitted) sits outside the initial assembly but
        // leads the tool loop's seed. The budgeter counts the prompt once either way, so the hard stop measures the actual request.
        var result = measured ?? _contextBudgeter.Budget(messages,
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
                "Conversation context budgeted for invocation {InvocationId} ({Stage}): dropped {Dropped} message(s), truncated {Truncated} tool result(s) ({Chars} chars), stripped reasoning from {ReasoningStripped} message(s), excerpted {ProtectedResultsExcerpted} protected tool result(s), estimated tokens {Before} -> {After}, capacity {Capacity} reserving {Reserved}, fixed overhead {FixedOverhead} (system prompt + tools), effective history budget {EffectiveBudget} (still over budget: {Overflow}).",
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
                result.FixedOverheadTokens,
                result.EffectiveBudgetTokens,
                result.ExceedsBudget);
        }

        if (result.ExceedsBudget)
        {
            // Compacting cannot help when the tool offer is what does not fit (model-matrix F5), so the message names it.
            var toolsAreTheCause = toolBudgetDefinitions.Count > 0
                                   && !_contextBudgeter.Budget(messages, turnPolicy.ContextCapacityTokens, turnPolicy.ReservedOutputTokens, package.ResolvedSystemPrompt, [], resolvedModel)
                                                       .ExceedsBudget;
            throw new ContextBudgetExceededException(toolsAreTheCause ? ToolOfferExceedsWindowMessage : ContextBudgetExceededMessage);
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
    internal static IReadOnlyList<InvocationSkill>? MapSkills(IReadOnlyList<ResolvedSkill>? skills)
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
    ///     swaps them — so the schema footprint is counted for every tool. The benchmark freeze renders the same
    ///     definitions for its pre-flight budget, so the refusal there and the hard stop here cannot disagree.
    /// </remarks>
    internal static IReadOnlyList<string> BuildToolBudgetDefinitions(IReadOnlyList<AllowedToolDto> allowedTools)
    {
        if (allowedTools.Count == 0)
        {
            return [];
        }

        return [.. allowedTools.Select(static tool => string.Concat(tool.Name, "\n", tool.Description, "\n", XE_Local_AI_Engine.Providers.Abstractions.Tokenization.TokenEstimatorCalibrationStore.RenderToolSchema(tool.ParameterSchema)))];
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
