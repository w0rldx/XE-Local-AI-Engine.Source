namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Provider-to-host seam for report-only llama-server load telemetry. The provider supplies a null implementation;
///     the application host bridges observations to its own meter without reversing the dependency direction.
/// </summary>
public interface ILlamaServerLoadTelemetry
{
    void RecordLoad(LlamaServerLoadObservation observation);

    /// <summary>Reports a deliberate teardown of a live process the supervisor owned. Report-only: an implementation must not throw.</summary>
    void RecordUnload(string modelName, ModelRole role, LlamaServerUnloadReason reason);
}
