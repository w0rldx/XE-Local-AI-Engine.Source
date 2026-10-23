namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Why the supervisor tore down a live llama-server it owned; reported through <see cref="ILlamaServerLoadTelemetry.RecordUnload" />.</summary>
public enum LlamaServerUnloadReason
{
    /// <summary>The idle reaper unloaded it after its idle time-to-live.</summary>
    IdleTimeout = 0,

    /// <summary>Cap admission unloaded it to make room for another model under the loaded-model cap.</summary>
    LoadedModelCap = 1,

    /// <summary>A cold chat load the capacity gate refused on memory unloaded it.</summary>
    MemoryForAnotherModel = 2,

    /// <summary>The supervisor shut down.</summary>
    Shutdown = 3
}
