namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     What the chat admission's eviction callback found: a model it unloaded, an otherwise eligible model only a request in flight
///     kept loaded, or neither.
/// </summary>
/// <param name="EvictedModel">Describes the model unloaded; <see langword="null" /> when nothing was unloaded.</param>
/// <param name="BusyModel">Describes an eligible model skipped only for its active lease; <see langword="null" /> when none.</param>
public readonly record struct IdleChatEvictionResult(string? EvictedModel, string? BusyModel)
{
    /// <summary>No chat model the load may unload, busy or not.</summary>
    public static IdleChatEvictionResult NothingEligible { get; } = new(EvictedModel: null, BusyModel: null);
}
