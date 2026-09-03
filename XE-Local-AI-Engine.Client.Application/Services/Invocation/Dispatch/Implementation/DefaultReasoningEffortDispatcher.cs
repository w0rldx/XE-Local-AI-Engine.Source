namespace XE_Local_AI_Engine.Client.Services.Invocation.Dispatch.Implementation;

using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;

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
public sealed class DefaultReasoningEffortDispatcher(IModelTrustResolver modelTrustResolver) : IReasoningEffortDispatcher
{
    // Why 4096 and not the low tier's own 2048: DeferredLlamaServerChatClient.ClampToGenerationRoom narrows a
    // reasoning budget to half the generation room, and the room is min(num_ctx, MaxOutputTokens). At a 1024 cap the
    // `low` budget of 2048 clamps to 512 and leaves at most 512 tokens of ANSWER — a FAST tier that truncates replies
    // rather than shortening reasoning. At 4096 the clamp is min(2048, 2048), i.e. the full `low` budget with about
    // 2048 tokens left for the answer. It also exceeds the 1024 reserved-output floor, which is what makes the
    // runner's TurnPolicy.WithDispatchedOutputBudget do real work rather than being a no-op.
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

    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));

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
            var binaryBudget = tier == ReasoningTier.Fast && await IsNodeLocalAsync(request, cancellationToken).ConfigureAwait(false)
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
        var resolvedIsLocal = await IsNodeLocalAsync(request, cancellationToken).ConfigureAwait(false);
        var swapRefusal = ResolveSwapRefusal(request, resolvedIsLocal);

        return Decide(request, tier, swapRefusal ?? tierReason, resolvedIsLocal ? FastGradedOutputTokens : null);
    }

    /// <summary>
    ///     Why the resolved model is NOT replaced on this FAST turn, or <see langword="null" /> when every gate passes.
    ///     Ordered cheapest-first: the pure request-shape gates run before any node-side lookup, so a turn that can
    ///     never swap pays for nothing, and the reason names the most specific rule that applies.
    /// </summary>
    private static string? ResolveSwapRefusal(ReasoningDispatchRequest request, bool resolvedIsLocal)
    {
        // The turn's data was admitted upstream against the resolved model's egress posture; replacing a cloud model
        // would move that data somewhere the gate never authorised.
        if (!resolvedIsLocal)
        {
            return ReasoningDispatchReasons.CloudNoSwap;
        }

        // Fail-closed: unknown provenance is `false`, so every construction site that never heard of this feature
        // refuses the swap without an edit.
        if (!request.AllowAutoModelSwap)
        {
            return ReasoningDispatchReasons.ModelPinned;
        }

        // The small model would have to drive a tool loop against an offer that was ranked, resolved and authorised
        // for the big one. The TIER still stands — this refuses only the swap.
        if (request.OfferedToolCount > 0)
        {
            return ReasoningDispatchReasons.ToolsNoSwap;
        }

        // The fast model's vision capability is a separate question from its reasoning depth, and the attachment
        // egress gate admitted the image against the resolved model.
        if (request.HasAttachments)
        {
            return ReasoningDispatchReasons.AttachmentsNoSwap;
        }

        // A skill-bearing turn expects the model to fetch and follow a skill body over progressive disclosure; a small
        // model handed that loop fails differently from one that simply answers more briefly.
        if (request.HasSkills)
        {
            return ReasoningDispatchReasons.SkillsNoSwap;
        }

        // The schema compiles to a grammar the swapped model was never chosen for.
        if (request.HasResponseSchema)
        {
            return ReasoningDispatchReasons.SchemaNoSwap;
        }

        // A scheduled run already holds a capacity reservation for its effective model; a second one double-books the
        // ledger and burns a loaded-process slot for a single turn.
        if (request.IsUnattended)
        {
            return ReasoningDispatchReasons.UnattendedNoSwap;
        }

        // No node names a FAST model yet: the node setting that would, and the locality and capacity gates that guard
        // it, are phase 3 of this slice. With no model named there is nothing to swap to, which is exactly what this
        // reason says — and it stays the answer on any node that leaves the setting blank.
        return ReasoningDispatchReasons.FastModelUnset;
    }

    private async Task<bool> IsNodeLocalAsync(ReasoningDispatchRequest request, CancellationToken cancellationToken)
    {
        return await _modelTrustResolver.ResolveAsync(request.ResolvedModel, cancellationToken).ConfigureAwait(false) == ModelTrustLocality.Local;
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
