namespace XE_Local_AI_Engine.Client.Services.Invocation.Dispatch.Implementation;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The single <see cref="IReasoningEffortDispatcher" />: a deterministic heuristic, no model call.
///     <see cref="ReasoningEffortSignals" /> owns the ladder that turns a turn's shape into a tier; this class turns
///     that tier into the concrete <c>{model, effort, output budget}</c> the runner applies, and owns the gates that
///     decide whether the model may be replaced at all.
///     <para>
///         <b>The tier is never demoted.</b> Nothing about a turn's contents moves it down. Five package members
///         (offered tools, attachments, skills, a response schema, an unattended run) make a turn ineligible for the
///         model SWAP while the tier stands, because less reasoning is safe where a different model is not.
///         <see cref="ReasoningTier.Fast" /> on the resolved model at <c>low</c> therefore stays reachable on a
///         tool-enabled turn, which is the whole point of the rule.
///     </para>
///     <para>
///         <b>Logging invariant.</b> <see cref="ReasoningDispatchDecision.ReasonCode" /> is the ONLY output of this
///         class that may ever be logged. No signal value — message length, conversation depth, the score — may
///         appear in a log message or a log scope even at Debug, and
///         <see cref="ReasoningDispatchRequest.LatestUserText" /> must never reach a log scope at all. That is what
///         keeps the slice inside the agent-trajectory data policy on the logging side. This class therefore takes no
///         logger.
///     </para>
/// </summary>
public sealed class DefaultReasoningEffortDispatcher(
    IModelTrustResolver modelTrustResolver,
    INodeRuntimeSettings nodeRuntimeSettings,
    ILocalModelProviderResolver localModelProviderResolver,
    ICapacityService capacityService,
    IModelCapabilityResolver modelCapabilityResolver,
    ILlamaServerProcessSupervisor processSupervisor) : IReasoningEffortDispatcher
{
    // A FAST turn caps the ANSWER, not the reasoning: DeferredLlamaServerChatClient.ClampToGenerationRoom narrows a
    // reasoning budget to half of min(num_ctx, MaxOutputTokens), and at 4096 that clamp is min(2048, 2048) — the full
    // `low` budget, with about 2048 tokens left for the answer. Below 4096 the clamp would start eating the reasoning
    // budget instead, which is the one thing the tier must not do.
    //
    // This is a CEILING the runner applies only when it fits in a quarter of the window the model actually launched
    // with, and only onto the send's sampling options. Both context budgeters derive their output RESERVATION from
    // the requested max-output-tokens, so an unconditional cap would shrink the history budget of every FAST turn and
    // fail outright on a window at or below 4096 — see InvocationRunner, where that gate lives.
    private const int FastGradedOutputTokens = 4096;

    // A binary-branch FAST turn suppresses reasoning outright, so the whole budget is answer.
    private const int FastBinaryOutputTokens = 2048;

    private const string LowEffort = "low";
    private const string MediumEffort = "medium";
    private const string HighEffort = "high";

    /// <summary>Binary-model reasoning OFF (<c>think:false</c>).</summary>
    private const string NoneEffort = "none";

    /// <summary>Binary-model reasoning ON (the think field is omitted so the chat template's own reasoning runs).</summary>
    private const string OnEffort = "on";

    private readonly ICapacityService _capacityService = capacityService ?? throw new ArgumentNullException(nameof(capacityService));
    private readonly ILocalModelProviderResolver _localModelProviderResolver = localModelProviderResolver ?? throw new ArgumentNullException(nameof(localModelProviderResolver));
    private readonly IModelCapabilityResolver _modelCapabilityResolver = modelCapabilityResolver ?? throw new ArgumentNullException(nameof(modelCapabilityResolver));
    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
    private readonly INodeRuntimeSettings _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
    private readonly ILlamaServerProcessSupervisor _processSupervisor = processSupervisor ?? throw new ArgumentNullException(nameof(processSupervisor));

    public async Task<ReasoningDispatchDecision> DispatchAsync(ReasoningDispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Hard rule 1. An orchestrated turn is many models' work behind one package; the tier belongs to no single one
        // of them, and the participants resolve their own efforts. Normal, never swapped, no score computed.
        if (request.HasOrchestration)
        {
            return Decide(request, ReasoningTier.Normal, ReasoningDispatchReasons.Orchestration, maxOutputTokens: null);
        }

        var (tier, tierReason) = ReasoningEffortSignals.Resolve(request.LatestUserText, request.HasAttachments, request.ConversationDepth);

        // Hard rule 2. A model with no graded ladder maps the tier onto the binary on/off pair and is never swapped:
        // a stale `auto` reaching one still has to mean something, and "reason, unless the turn is trivial" is it.
        // Reported as `binary-model` so the notice says WHY the effort is not one of the graded levels.
        if (!request.SupportsThinking)
        {
            var binaryBudget = tier == ReasoningTier.Fast && await IsNodeLocalAsync(request.ResolvedModel, cancellationToken).ConfigureAwait(false)
                ? FastBinaryOutputTokens
                : (int?)null;

            return Decide(request, tier, ReasoningDispatchReasons.BinaryModel, binaryBudget);
        }

        // Everything below is FAST-only: the other two tiers carry no output budget and are never swapped, so they
        // cost no trust lookup, no settings read and no capacity probe.
        if (tier != ReasoningTier.Fast)
        {
            return Decide(request, tier, tierReason, maxOutputTokens: null);
        }

        // One lookup, used twice: it caps the output budget (a cloud model's budget is the provider's business) and it
        // is swap gate 1. ModelTrustLocality treats `Unresolved` exactly as `Cloud`, so `== Local` is fail-closed by
        // construction.
        var resolvedIsLocal = await IsNodeLocalAsync(request.ResolvedModel, cancellationToken).ConfigureAwait(false);
        var swap = await ResolveSwapAsync(request, resolvedIsLocal, cancellationToken).ConfigureAwait(false);
        if (swap.FastModel is null)
        {
            return Decide(request, tier, swap.RefusalReason ?? tierReason, resolvedIsLocal ? FastGradedOutputTokens : null);
        }

        return await DecideSwappedAsync(request, tier, tierReason, swap, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The swapped decision: the FAST model's OWN capability flags, re-resolved, because the resolved model's are
    ///     now stale — a stale <c>ReasoningBudgetEnforceable</c> sends a budget the replacement 400s on, and a stale
    ///     <c>SupportsThinking</c> picks an effort from the wrong ladder. The reason stays the TIER reason: every gate
    ///     passed, so there is no rule to name.
    /// </summary>
    private async Task<ReasoningDispatchDecision> DecideSwappedAsync(ReasoningDispatchRequest request,
        ReasoningTier tier,
        string tierReason,
        SwapResolution swap,
        CancellationToken cancellationToken)
    {
        var fastModel = swap.FastModel!;
        var capabilities = await _modelCapabilityResolver.ResolveAsync(fastModel, cancellationToken).ConfigureAwait(false);
        var budget = capabilities.SupportsThinking ? FastGradedOutputTokens : FastBinaryOutputTokens;
        var budgetKept = request.HasExplicitOutputBudget;

        return new ReasoningDispatchDecision(tier,
            fastModel,
            ResolveEffort(capabilities.SupportsThinking, tier),
            budgetKept ? null : budget,
            capabilities.SupportsThinking,
            capabilities.ReasoningBudgetEnforceable,
            budgetKept ? tierReason + ReasoningDispatchReasons.ExplicitBudgetKeptSuffix : tierReason,
            swap.Reservation);
    }

    /// <summary>
    ///     Either the node-local FAST model this turn may be moved onto (with the ledger reservation its admission
    ///     produced, when it produced one), or the reason it may not be. Ordered cheapest-first: the pure request-shape
    ///     gates run before any node-side lookup, so a turn that can never swap pays for no settings read, no trust
    ///     lookup and no capacity probe, and the reason names the most specific rule that applies.
    ///     <para>
    ///         It never propagates a node-side failure. The dispatcher's contract is that it never fails a turn — a
    ///         settings read, a provider lookup or a capacity probe that throws means "this node cannot serve a swap
    ///         right now", which is exactly <see cref="ReasoningDispatchReasons.FastModelUnavailable" />. A
    ///         cancellation still propagates, because the turn itself is terminating.
    ///     </para>
    /// </summary>
    private async Task<SwapResolution> ResolveSwapAsync(ReasoningDispatchRequest request,
        bool resolvedIsLocal,
        CancellationToken cancellationToken)
    {
        // The turn's data was admitted upstream against the resolved model's egress posture; replacing a cloud model
        // would move that data somewhere the gate never authorised.
        if (!resolvedIsLocal)
        {
            return SwapResolution.Refused(ReasoningDispatchReasons.CloudNoSwap);
        }

        // Fail-closed: unknown provenance is `false`, so every construction site that never heard of this feature
        // refuses the swap without an edit.
        if (!request.AllowAutoModelSwap)
        {
            return SwapResolution.Refused(ReasoningDispatchReasons.ModelPinned);
        }

        // The small model would have to drive a tool loop against an offer that was ranked, resolved and authorised
        // for the big one. The TIER still stands — this refuses only the swap.
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

        // A skill-bearing turn expects the model to fetch and follow a skill body over progressive disclosure; a small
        // model handed that loop fails differently from one that simply answers more briefly.
        if (request.HasSkills)
        {
            return SwapResolution.Refused(ReasoningDispatchReasons.SkillsNoSwap);
        }

        // The schema compiles to a grammar the swapped model was never chosen for.
        if (request.HasResponseSchema)
        {
            return SwapResolution.Refused(ReasoningDispatchReasons.SchemaNoSwap);
        }

        // A scheduled run already holds a capacity reservation for its effective model; a second one double-books the
        // ledger and burns a loaded-process slot for a single turn.
        if (request.IsUnattended)
        {
            return SwapResolution.Refused(ReasoningDispatchReasons.UnattendedNoSwap);
        }

        try
        {
            var fastModel = await _nodeRuntimeSettings.GetAutoEffortFastModelNameAsync(cancellationToken).ConfigureAwait(false);

            // The answer on every node that leaves the setting blank, which is the shipped default.
            if (string.IsNullOrWhiteSpace(fastModel))
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.FastModelUnset);
            }

            if (string.Equals(fastModel, request.ResolvedModel, StringComparison.OrdinalIgnoreCase))
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.FastModelIsActiveModel);
            }

            // Enforcement point 2 of the node-locality gate. The same pair was checked when the operator saved the
            // setting; it runs again here because a model can be uninstalled, or an external connection re-declared,
            // between that save and this turn. The turn's data was admitted upstream against a node-local model, and
            // an external or cloud replacement would carry it somewhere no egress gate authorised.
            if (!await IsNodeLocalAsync(fastModel, cancellationToken).ConfigureAwait(false)
                || !await IsLlamaServerModelAsync(fastModel, cancellationToken).ConfigureAwait(false))
            {
                return SwapResolution.Refused(ReasoningDispatchReasons.FastModelNotLocal);
            }

            var capacity = await _capacityService.DecideAsync(new CapacityRequest(fastModel, ModelRole.Chat), cancellationToken).ConfigureAwait(false);
            return capacity.Verdict switch
            {
                // A fresh launch was admitted, so no process for the fast key exists to be profiling-owned. The
                // reservation books the model's bytes and one loaded-process slot; the RUNNER owns releasing it.
                CapacityVerdict.Allow => SwapResolution.Admitted(fastModel, capacity.Reservation),

                // A process for the key already exists — but the running snapshot capacity read it from does not
                // filter profiling-owned or draining processes, so "already running" is not yet "can serve this turn".
                // The lease is the interlock that answers that: it is granted only for a live, non-exited,
                // non-profiling-owned, non-evicting process, and it re-checks both after acquiring. Take it, read the
                // shape, release it immediately — the send takes its own.
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
        return await _modelTrustResolver.ResolveAsync(model, cancellationToken).ConfigureAwait(false) == ModelTrustLocality.Local;
    }

    private async Task<bool> IsLlamaServerModelAsync(string model, CancellationToken cancellationToken)
    {
        var providerName = await _localModelProviderResolver.ResolveProviderNameForModelAsync(model, cancellationToken).ConfigureAwait(false);
        return string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Builds the no-swap decision: the resolved model keeps its capability flags, and a FAST output budget yields
    ///     to a developer-gated per-send budget (the send asked for a specific ceiling; the dispatcher's is a default).
    /// </summary>
    private static ReasoningDispatchDecision Decide(ReasoningDispatchRequest request, ReasoningTier tier, string reasonCode, int? maxOutputTokens)
    {
        var budgetKept = maxOutputTokens is not null && request.HasExplicitOutputBudget;

        return new ReasoningDispatchDecision(tier,
            request.ResolvedModel,
            ResolveEffort(request.SupportsThinking, tier),
            budgetKept ? null : maxOutputTokens,
            request.SupportsThinking,
            request.ReasoningBudgetEnforceable,
            budgetKept ? reasonCode + ReasoningDispatchReasons.ExplicitBudgetKeptSuffix : reasonCode,
            CapacityReservation: null);
    }

    /// <summary>
    ///     The swap ladder's answer: either a FAST model (with the reservation its admission produced, if any) or the
    ///     reason there is none. Exactly one of the two is set.
    /// </summary>
    private readonly record struct SwapResolution(string? FastModel, string? RefusalReason, IDisposable? Reservation)
    {
        public static SwapResolution Refused(string reason) => new(FastModel: null, reason, Reservation: null);

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
