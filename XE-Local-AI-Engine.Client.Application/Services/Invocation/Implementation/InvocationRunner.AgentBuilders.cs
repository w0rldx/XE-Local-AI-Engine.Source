namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Providers.Ollama;
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
        CancellationToken invocationToken)
    {
        var participants = new List<OrchestrationParticipant>(spec.Participants.Count);
        foreach (var participant in spec.Participants)
        {
            var participantModel = await ResolveModelAsync(participant.ModelId ?? resolvedModel, invocationToken).ConfigureAwait(false);
            participants.Add(new OrchestrationParticipant
            {
                Key = participant.Key,
                Name = participant.Name,
                Description = participant.Description,
                Instructions = participant.Instructions,
                ModelId = participantModel,
                ReasoningEffort = participant.ReasoningEffort,
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
                ToolLocation.ClientLocal => InvocationToolBridge.CreateOfferPlaceholder(tool.Name),
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

    private async Task<string> ResolveModelAsync(string? requestedModel, CancellationToken cancellationToken)
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
                return trimmedModel;
            }
        }

        if (await _capabilityReporter.VerifyOllamaAndModelAsync(trimmedModel, cancellationToken).ConfigureAwait(false))
        {
            return string.IsNullOrWhiteSpace(trimmedModel) ? _defaultModel : trimmedModel;
        }

        if (string.IsNullOrWhiteSpace(trimmedModel))
        {
            throw new InvalidOperationException("Ollama is unavailable or the default model is not installed.");
        }

        _logger.LogWarning("Requested model '{RequestedModel}' could not be verified. Falling back to '{FallbackModel}'.",
            trimmedModel,
            _defaultModel);

        return _defaultModel;
    }

    private InvocationAgentDefinition BuildInvocationDefinition(RuntimePackage package, string resolvedModel)
    {
        var messages = BuildChatMessages(package);

        return new InvocationAgentDefinition(resolvedModel,
            package.ResolvedSystemPrompt,
            BuildInvocationTools(package),
            messages,
            package.ReasoningEffort,
            package.SupportsThinking,
            MapSamplingOptions(package.SamplingOptions),
            MapSkills(package.Skills));
    }

    /// <summary>
    ///     Maps the resolved client-side <see cref="ResolvedSkill" /> set onto the provider-agnostic
    ///     <see cref="InvocationSkill" /> records the factory builds into a MAF <c>AgentSkillsProvider</c> (.AI.Agent
    ///     cannot reference Client.Models). Returns null for a null/empty set so the no-skills path stays byte-identical
    ///     (the factory keeps the existing positional constructor and attaches no context provider).
    /// </summary>
    private static IReadOnlyList<InvocationSkill>? MapSkills(IReadOnlyList<ResolvedSkill>? skills)
    {
        if (skills is not { Count: > 0 })
        {
            return null;
        }

        return [.. skills.Select(static skill => new InvocationSkill(skill.Name, skill.Description, skill.Body))];
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
            Seed = sampling.Seed,
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
                ToolLocation.ClientLocal => InvocationToolBridge.CreateOfferPlaceholder(tool.Name),
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
