namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>How much of a model's expert weights a replayed <c>-ot</c> pins to system RAM (<see cref="LlamaServerManagedFlags.ClassifyExpertPin" />).</summary>
public enum ExpertPinScope
{
    /// <summary>No expert tensor is pinned to the CPU.</summary>
    None = 0,

    /// <summary>Every expert tensor, as <c>--cpu-moe</c> places them: the pattern without a block index.</summary>
    AllExperts = 1,

    /// <summary>Some expert tensors only: a block-indexed pattern, a mix, or any other <c>exps=CPU</c> form.</summary>
    Partial = 2
}
