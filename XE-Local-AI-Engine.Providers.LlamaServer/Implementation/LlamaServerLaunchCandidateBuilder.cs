namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Builds the ordered primary and safe-fallback launch candidates for one process spawn.</summary>
internal sealed class LlamaServerLaunchCandidateBuilder(
    IProcessContextAllocationResolver allocationResolver,
    ILlamaServerLaunchPolicy launchPolicy)
{
    public async Task<LlamaServerLaunchPlanSet> BuildAsync(LlamaServerProcessSupervisor.ProcessKey key,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        bool applyLaunchPolicy,
        ProcessContextAllocation? admittedAllocation,
        Func<string, LlamaRuntimeException> reject,
        CancellationToken ct)
    {
        if (!applyLaunchPolicy)
        {
            LlamaServerLaunchPlan? cpuReplayPlan = variant == GpuVariant.Cpu && !resolved.ExploreMode
                ? launchPolicy.ResolveCpuReplayPlan(resolved)
                : null;
            return new LlamaServerLaunchPlanSet(null,
                [new LlamaServerLaunchCandidate(resolved, cpuReplayPlan, LlamaServerLoadAttemptKind.Primary)]);
        }

        ProcessContextAllocation allocation;
        if (admittedAllocation is null)
        {
            allocation = await allocationResolver.ResolveAsync(key.ModelName, key.Role, variant, resolved, ct).ConfigureAwait(false)
                         ?? throw reject("The requested model's process context could not be allocated.");
        }
        else if (admittedAllocation.Source == ProcessContextAllocationSource.HardwareTier)
        {
            if (!allocationResolver.TryGetEffectiveCommittedAllocation(admittedAllocation, out allocation)
                || !string.Equals(allocation.CacheKey, admittedAllocation.CacheKey, StringComparison.Ordinal)
                || !string.Equals(allocation.ContentIdentity, admittedAllocation.ContentIdentity, StringComparison.Ordinal)
                || allocation.ProcessContextTokens > admittedAllocation.ProcessContextTokens)
            {
                throw reject("The admitted local model context allocation is no longer valid.");
            }
        }
        else
        {
            allocation = admittedAllocation;
        }

        var plan = await launchPolicy.ResolveAsync(key.Role, variant, resolved, allocation, ct).ConfigureAwait(false);

        // INVARIANT: the only optimization a safe candidate may drop is KV-cache quantization. --cpu-moe is carried
        // through untouched, because dropping it would launch the over-subscription the capability gate refuses; and a
        // plan carrying ONLY --cpu-moe therefore gets no retry at all — there is nothing safe left to drop. That is
        // what keeps every safe retry a KV retry, which is what makes the supervisor's fallback attribution sound.
        if (plan.UseKvCacheQuantization)
        {
            return new LlamaServerLaunchPlanSet(allocation,
            [
                new LlamaServerLaunchCandidate(resolved, plan, LlamaServerLoadAttemptKind.Primary),
                new LlamaServerLaunchCandidate(resolved, plan.WithoutKvCacheQuantization(), LlamaServerLoadAttemptKind.SafeRetry)
            ]);
        }

        if (variant != GpuVariant.Cpu && !resolved.ExploreMode && !string.IsNullOrWhiteSpace(resolved.KvTypeK))
        {
            return new LlamaServerLaunchPlanSet(allocation,
            [
                new LlamaServerLaunchCandidate(resolved, plan, LlamaServerLoadAttemptKind.Primary),
                new LlamaServerLaunchCandidate(resolved.WithoutKvCacheQuantization(), plan, LlamaServerLoadAttemptKind.SafeRetry)
            ]);
        }

        return new LlamaServerLaunchPlanSet(allocation,
            [new LlamaServerLaunchCandidate(resolved, plan, LlamaServerLoadAttemptKind.Primary)]);
    }
}

internal sealed record LlamaServerLaunchCandidate(
    ResolvedLaunchArguments Resolved,
    LlamaServerLaunchPlan? Plan,
    LlamaServerLoadAttemptKind AttemptKind);

internal sealed record LlamaServerLaunchPlanSet(
    ProcessContextAllocation? Allocation,
    List<LlamaServerLaunchCandidate> Candidates);
