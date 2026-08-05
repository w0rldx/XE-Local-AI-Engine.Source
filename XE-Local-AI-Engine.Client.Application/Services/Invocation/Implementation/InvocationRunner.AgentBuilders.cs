namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
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
    ///                 (<see cref="ResolveEffectiveContextTokensAsync" /> ran once in <see cref="PrepareLocalRuntimeAsync" />)
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

        var provider = await ResolveWarmableProviderAsync(participantModel, invocationId, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return null;
        }

        return await ResolveEffectiveContextTokensAsync(provider, participantModel, invocationId, cancellationToken).ConfigureAwait(false);
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
                    (arguments, cancellationToken) => ExecuteApiToolCallAsync(package.InvocationId, tool.Name, arguments, tool.RequiresApproval, cancellationToken)),
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
            effectiveContextTokens);
    }

    /// <summary>
    ///     Applies the conversation-context budget (using the turn's already-resolved <see cref="TurnPolicy.ContextCapacityTokens" />/
    ///     <see cref="TurnPolicy.ReservedOutputTokens" />, kept in lockstep with the factory's num_ctx / output-clamp
    ///     source) to a message list. The FIRST time a trim occurs in an invocation this logs once (unchanged) AND
    ///     emits a single sanitized <see cref="TurnNoticeKind.HistoryTruncated" /> chat notice carrying counts only —
    ///     never content. When the budgeter still reports <c>ExceedsBudget</c> after its two-pass truncation, this is a
    ///     HARD STOP: it throws <see cref="ContextBudgetExceededException" /> (a classified, pre-inference failure —
    ///     see <see cref="MapFailure" />) instead of proceeding with an over-budget send. Returns the input unchanged
    ///     (reference-equal) when nothing was trimmed.
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> ApplyContextBudgetAsync(IReadOnlyList<ChatMessage> messages,
        RuntimePackage package,
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
            BuildToolBudgetDefinitions(package),
            resolvedModel);

        if (!result.Trimmed && !result.ExceedsBudget)
        {
            return result.Messages;
        }

        if (!gate.Logged)
        {
            gate.Logged = true;
            _logger.LogWarning(
                "Conversation context budgeted for invocation {InvocationId} ({Stage}): dropped {Dropped} message(s), truncated {Truncated} tool result(s) ({Chars} chars), estimated tokens {Before} -> {After}, capacity {Capacity} reserving {Reserved} (still over budget: {Overflow}).",
                package.InvocationId,
                stage,
                result.MessagesDropped,
                result.ToolResultsTruncated,
                result.CharsTruncated,
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

    /// <summary>Sanitized, user-facing text for a <see cref="TurnNoticeKind.HistoryTruncated" /> notice — counts only, never content.</summary>
    private static string BuildHistoryTruncatedNoticeMessage(ConversationBudgetResult result)
    {
        return
            $"Conversation history was trimmed to fit the model's context window ({result.MessagesDropped} older message(s) dropped, {result.ToolResultsTruncated} tool result(s) shortened). The originals are kept — use Compact to summarize older messages and preserve their context.";
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

    private static IReadOnlyList<ChatMessage> BuildChatMessages(RuntimePackage package)
    {
        return package.ConversationContext
                      .OrderBy(message => message.SortOrder)
                      .Select(static message =>
                      {
                          var contents = new List<AIContent>();
                          if (!string.IsNullOrEmpty(message.Thinking))
                          {
                              contents.Add(new TextReasoningContent(message.Thinking));
                          }

                          contents.Add(new TextContent(message.Content));
                          return new ChatMessage(MapRole(message.Role), contents);
                      })
                      .ToList();
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
                    (arguments, cancellationToken) => ExecuteApiToolCallAsync(package.InvocationId, tool.Name, arguments, tool.RequiresApproval, cancellationToken)),
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
