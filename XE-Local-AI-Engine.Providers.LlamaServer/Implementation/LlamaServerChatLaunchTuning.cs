namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Options;

internal readonly record struct LlamaServerChatLaunchTuning(
    int ChatCacheReuse,
    int ChatCacheRamMiB,
    SpeculativeDecodingSettings Speculative);
