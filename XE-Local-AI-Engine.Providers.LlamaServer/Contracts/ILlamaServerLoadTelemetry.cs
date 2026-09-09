namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Provider-to-host seam for report-only llama-server load telemetry. The provider supplies a null implementation;
///     the application host bridges observations to its own meter without reversing the dependency direction.
/// </summary>
public interface ILlamaServerLoadTelemetry
{
    void RecordLoad(LlamaServerLoadObservation observation);
}
