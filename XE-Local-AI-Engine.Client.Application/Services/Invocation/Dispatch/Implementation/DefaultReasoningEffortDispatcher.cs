namespace XE_Local_AI_Engine.Client.Services.Invocation.Dispatch.Implementation;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The single <see cref="IReasoningEffortDispatcher" />: a deterministic heuristic with no model call, turning
///     the tier <see cref="ReasoningEffortSignals" /> resolved into a concrete model and effort, and owning the gates
///     that decide whether the model may be replaced at all.
/// </summary>
/// <remarks>
///     The tier is NEVER demoted by a turn's contents. Five package members — offered tools, attachments, skills, a
///     response schema, an unattended run — only make a turn ineligible for the model SWAP, because less reasoning is
///     safe where a different model is not, which keeps <see cref="ReasoningTier.Fast" /> reachable with tools on.
///     LOGGING INVARIANT: <see cref="ReasoningDispatchDecision.ReasonCode" /> is the only output that may ever be
///     logged — no signal value, and never <see cref="ReasoningDispatchRequest.LatestUserText" />. Hence no logger.
/// </remarks>
public sealed class DefaultReasoningEffortDispatcher : IReasoningEffortDispatcher
{
    // NO TIER CAPS THE OUTPUT: DeferredLlamaServerChatClient.ClampToGenerationRoom already halves a reasoning budget,
    // so a FAST cap changed nothing there while costing history — both budgeters reserve output from max-output-tokens.

    private const string LowEffort = "low";
    private const string MediumEffort = "medium";
    private const string HighEffort = "high";

    /// <summary>Binary-model reasoning OFF (<c>think:false</c>).</summary>
    private const string NoneEffort = "none";

    /// <summary>Binary-model reasoning ON (the think field is omitted so the chat template's own reasoning runs).</summary>
    private const string OnEffort = "on";

    private readonly ICapacityService _capacityService;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ILocalModelProviderResolver _localModelProviderResolver;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly INodeRuntimeSettings _nodeRuntimeSettings;
    private readonly ILlamaServerProcessSupervisor _processSupervisor;

    public DefaultReasoningEffortDispatcher(
        IModelTrustResolver modelTrustResolver,
        INodeRuntimeSettings nodeRuntimeSettings,
        ILocalModelProviderResolver localModelProviderResolver,
        ICapacityService capacityService,
        IModelCapabilityResolver modelCapabilityResolver,
        ILlamaServerProcessSupervisor processSupervisor,
        IGgufModelStore ggufModelStore)
    {
        ArgumentNullException.ThrowIfNull(capacityService);
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(localModelProviderResolver);
        ArgumentNullException.ThrowIfNull(modelCapabilityResolver);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        ArgumentNullException.ThrowIfNull(nodeRuntimeSettings);
        ArgumentNullException.ThrowIfNull(processSupervisor);
        _capacityService = capacityService;
        _ggufModelStore = ggufModelStore;
        _localModelProviderResolver = localModelProviderResolver;
        _modelCapabilityResolver = modelCapabilityResolver;
        _modelTrustResolver = modelTrustResolver;
        _nodeRuntimeSettings = nodeRuntimeSettings;
        _processSupervisor = processSupervisor;
    }

    public async Task<ReasoningDispatchDecision> DispatchAsync(ReasoningDispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Hard rule 1. An orchestrated turn is many models' work behind one package; the tier belongs to no single one
        // of them, and the participants resolve their own efforts. Normal, never swapped, no score computed.
        if (request.HasOrchestration)
        {
            return Decide(request, ReasoningTier.Normal, ReasoningDispatchReasons.Orchestration);
        }

        var (tier, tierReason) = ReasoningEffortSignals.Resolve(request.LatestUserText, request.HasAttachments, request.ConversationDepth);

        // Hard rule 2. A model with no graded ladder maps the tier onto the binary pair and never swaps: a stale
        // `auto` must still mean "reason, unless the turn is trivial". Reported as `binary-model` so the notice says why.
        if (!request.SupportsThinking)
        {
            return Decide(request, tier, ReasoningDispatchReasons.BinaryModel);
        }

        // Everything below is FAST-only: the other two tiers are never swapped, so they cost no trust lookup, no
        // settings read and no capacity probe.
        if (tier != ReasoningTier.Fast)
        {
            return Decide(request, tier, tierReason);
        }

        var swap = await ResolveSwapAsync(request, cancellationToken);
        if (swap.FastModel is null)
        {
            return Decide(request, tier, swap.RefusalReason ?? tierReason);
        }

        // OWNERSHIP OF THE RESERVATION TRANSFERS ONLY WITH A RETURNED DECISION: admission already booked the bytes and
        // a slot, so every non-success exit releases it or the ledger wrongly rejects later admissions.
        try
        {
            return await DecideSwappedAsync(tier, tierReason, swap, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The turn itself is terminating, so the cancellation propagates exactly as it does out of the swap
            // ladder below — but the booking is still ours to release.
            swap.Reservation?.Dispose();
            throw;
        }
        catch (Exception)
        {
            // Same fail-soft contract as the swap ladder: a capability lookup that throws means "this node cannot
            // serve a swap right now", never a failed turn.
            swap.Reservation?.Dispose();
            return Decide(request, tier, ReasoningDispatchReasons.FastModelUnavailable);
        }
    }

    /// <summary>
    ///     The swapped decision, on the FAST model's OWN capability flags, re-resolved because the resolved model's
    ///     are now stale.
    /// </summary>
    /// <remarks>
    ///     A stale <c>ReasoningBudgetEnforceable</c> sends a budget the replacement 400s on, and a stale
    ///     <c>SupportsThinking</c> picks an effort from the wrong ladder. The reason stays the TIER reason: every gate
    ///     passed, so there is no rule to name.
    /// </remarks>
    private async Task<ReasoningDispatchDecision> DecideSwappedAsync(ReasoningTier tier,
        string tierReason,
        SwapResolution swap,
        CancellationToken cancellationToken)
    {
        var fastModel = swap.FastModel!;
        var capabilities = await _modelCapabilityResolver.ResolveAsync(fastModel, cancellationToken);

        return new ReasoningDispatchDecision
        {
            Tier = tier,
            Model = fastModel,
            Effort = ResolveEffort(capabilities.SupportsThinking, tier),
            MaxOutputTokens = null,
            SupportsThinking = capabilities.SupportsThinking,
            ReasoningBudgetEnforceable = capabilities.ReasoningBudgetEnforceable,
            ReasonCode = tierReason,
            CapacityReservation = swap.Reservation
        };
    }

    /// <summary>
    ///     Either the node-local FAST model this turn may be moved onto, with the ledger reservation its admission
    ///     produced, or the reason it may not be.
    /// </summary>
    /// <remarks>
    ///     Ordered so the reason names the most specific rule that applies and a turn that can never swap pays for no
    ///     settings read or capacity probe. It never propagates a node-side failure: a trust lookup, settings read,
    ///     provider lookup or capacity probe that throws means
    ///     <see cref="ReasoningDispatchReasons.FastModelUnavailable" />, so every node-side call sits INSIDE the try,
    ///     the first locality gate included. A cancellation still propagates — the turn itself is terminating.
    /// </remarks>
    private async Task<SwapResolution> ResolveSwapAsync(ReasoningDispatchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Swap gate 1. The turn's data was admitted against the resolved model's egress posture, so replacing a
            // cloud model would move it where no gate authorised. Unresolved trust counts as cloud: fail-closed.
            if (!await IsNodeLocalAsync(request.ResolvedModel, cancellationToken))
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.CloudNoSwap);
            }

            // Fail-closed: unknown provenance is `false`, so every construction site that never heard of this feature
            // refuses the swap without an edit.
            if (!request.AllowAutoModelSwap)
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.ModelPinned);
            }

            // The small model would have to drive a tool loop against an offer that was ranked, resolved and
            // authorised for the big one. The TIER still stands — this refuses only the swap.
            if (request.OfferedToolCount > 0)
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.ToolsNoSwap);
            }

            // The fast model's vision capability is a separate question from its reasoning depth, and the attachment
            // egress gate admitted the image against the resolved model.
            if (request.HasAttachments)
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.AttachmentsNoSwap);
            }

            // A skill-bearing turn expects the model to fetch and follow a skill body over progressive disclosure; a
            // small model handed that loop fails differently from one that simply answers more briefly.
            if (request.HasSkills)
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.SkillsNoSwap);
            }

            // The schema compiles to a grammar the swapped model was never chosen for.
            if (request.HasResponseSchema)
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.SchemaNoSwap);
            }

            // A scheduled run already holds a capacity reservation for its effective model; a second one double-books
            // the ledger and burns a loaded-process slot for a single turn.
            if (request.IsUnattended)
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.UnattendedNoSwap);
            }

            var fastModel = await _nodeRuntimeSettings.GetAutoEffortFastModelNameAsync(cancellationToken);

            // The answer on every node that leaves the setting blank, which is the shipped default.
            if (string.IsNullOrWhiteSpace(fastModel))
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.FastModelUnset);
            }

            if (string.Equals(fastModel, request.ResolvedModel, StringComparison.OrdinalIgnoreCase))
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.FastModelIsActiveModel);
            }

            // Enforcement point 2 of the node-locality gate, the SAME predicate the save ran, re-run because a model
            // can be uninstalled or re-declared since. An uninstalled fast model degrades HERE, by name, not at warm.
            if (!await NodeLocalModelGate
                       .IsInstalledNodeLocalLlamaModelAsync(fastModel, _ggufModelStore, _modelTrustResolver, _localModelProviderResolver, cancellationToken))
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.FastModelNotLocal);
            }

            var capacity = await _capacityService.DecideAsync(new CapacityRequest { ModelName = fastModel, Role = ModelRole.Chat }, cancellationToken);
            return capacity.Verdict switch
            {
                // A fresh launch was admitted, so no process for the fast key exists to be profiling-owned. The
                // reservation books the model's bytes and one loaded-process slot; the RUNNER owns releasing it.
                CapacityVerdict.Allow => SwapResolution.Admitted(fastModel, capacity.Reservation),

                // A process exists, but the snapshot does not filter profiling-owned or draining ones, so "running" is
                // not "can serve this turn". The lease answers that; take it, read the shape, release — the send re-takes.
                CapacityVerdict.QueueSameModel => ProbeRunningProcess(fastModel),
                _ => SwapResolution.Refused(ReasoningDispatchReasons.FastModelNoCapacity)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SwapResolution.Refused(ReasoningDispatchReasons.FastModelUnavailable);
        }
    }

    /// <summary>
    ///     The liveness probe on the already-running branch: a granted lease, disposed at once, is the only proof the
    ///     FAST model's process can actually serve a turn. Every other shape means the process exists but is not ours
    ///     to send to.
    /// </summary>
    private SwapResolution ProbeRunningProcess(string fastModel)
    {
        var acquisition = _processSupervisor.TryAcquireInferenceLease(fastModel, ModelRole.Chat);
        if (acquisition.Lease is null)
        {
            return SwapResolution.Refused(ReasoningDispatchReasons.FastModelUnavailable);
        }

        acquisition.Lease.Dispose();

        // QueueSameModel carries no reservation: nothing is being loaded, so there is nothing to book or release.
        return SwapResolution.Admitted(fastModel, reservation: null);
    }

    private async Task<bool> IsNodeLocalAsync(string model, CancellationToken cancellationToken)
    {
        return await _modelTrustResolver.ResolveAsync(model, cancellationToken) == ModelTrustLocality.Local;
    }

    /// <summary>
    ///     Builds the no-swap decision: the resolved model, its own capability flags, and the tier's effort. Nothing
    ///     else about the turn changes.
    /// </summary>
    private static ReasoningDispatchDecision Decide(ReasoningDispatchRequest request, ReasoningTier tier, string reasonCode)
    {
        return new ReasoningDispatchDecision
        {
            Tier = tier,
            Model = request.ResolvedModel,
            Effort = ResolveEffort(request.SupportsThinking, tier),
            MaxOutputTokens = null,
            SupportsThinking = request.SupportsThinking,
            ReasoningBudgetEnforceable = request.ReasoningBudgetEnforceable,
            ReasonCode = reasonCode,
            CapacityReservation = null
        };
    }

    /// <summary>
    ///     The swap ladder's answer: either a FAST model (with the reservation its admission produced, if any) or the
    ///     reason there is none. Exactly one of the two is set.
    /// </summary>
    private readonly record struct SwapResolution(string? FastModel, string? RefusalReason, IDisposable? Reservation)
    {
        public static SwapResolution Refused(string reason) =>
            new(FastModel: null, reason, Reservation: null);

        public static SwapResolution Admitted(string fastModel, IDisposable? reservation) =>
            new(fastModel, RefusalReason: null, reservation);
    }

    private static string ResolveEffort(bool supportsThinking, ReasoningTier tier)
    {
        if (!supportsThinking)
        {
            return tier == ReasoningTier.Fast ? NoneEffort : OnEffort;
        }

        return tier switch
        {
            ReasoningTier.Fast => LowEffort,
            ReasoningTier.Deep => HighEffort,
            _ => MediumEffort
        };
    }
}
