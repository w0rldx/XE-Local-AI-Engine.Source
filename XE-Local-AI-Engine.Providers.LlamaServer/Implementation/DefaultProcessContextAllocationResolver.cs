namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

internal sealed class DefaultProcessContextAllocationResolver(LlamaServerLaunchPolicyOptions options) : IProcessContextAllocationResolver
{
    private readonly LlamaServerLaunchPolicyOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public Task<ProcessContextAllocation?> ResolveAsync(string modelName,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        CancellationToken ct)
    {
        var frozen = !resolved.ExploreMode && resolved.CtxSize > 0;
        var overridden = _options.DeterministicContextTokensOverride is > 0;
        var tokens = _options.ContextTokensForRole(role);
        var source = ProcessContextAllocationSource.HardwareTier;
        if (overridden)
        {
            tokens = _options.DeterministicContextTokensOverride!.Value;
            source = ProcessContextAllocationSource.DeterministicOverride;
        }

        if (frozen)
        {
            tokens = resolved.CtxSize;
            source = ProcessContextAllocationSource.FrozenProfile;
        }

        return Task.FromResult<ProcessContextAllocation?>(new ProcessContextAllocation(tokens,
            ModelTrainContextTokens: null,
            source,
            variant == GpuVariant.Cpu ? ProcessPlacementMode.Cpu : ProcessPlacementMode.GpuResident,
            ResourceFootprint.Zero,
            modelName,
            $"{modelName}|{role}|{variant}|{tokens}"));
    }

    public bool TryDownTierAfterOutOfMemory(ProcessContextAllocation current, out ProcessContextAllocation downTiered)
    {
        downTiered = current;
        return false;
    }
}
