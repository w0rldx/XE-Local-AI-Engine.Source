namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Immutable parser output used by focused capability tests.</summary>
internal sealed class ParsedLlamaServerHelp
{
    public required IReadOnlySet<string> Options { get; init; }

    public required IReadOnlySet<string> SpeculativeModes { get; init; }

    public required IReadOnlySet<string> CacheTypesK { get; init; }

    public required IReadOnlySet<string> CacheTypesV { get; init; }

    public required IReadOnlySet<string> FlashAttentionModes { get; init; }
}
