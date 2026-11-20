namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>What a steering job takes over from its session once the derive's worker is gone: the gate and the lease its arms run under.</summary>
internal sealed class ModelLabArmsHolds
{
    public required IDisposable Gate { get; init; }

    public required ILlamaServerRuntimeMutationLease Lease { get; init; }
}
