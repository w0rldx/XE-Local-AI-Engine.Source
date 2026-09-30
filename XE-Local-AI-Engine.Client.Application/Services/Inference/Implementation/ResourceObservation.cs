namespace XE_Local_AI_Engine.Client.Services.Inference.Implementation;

internal sealed record ResourceObservation
{
    public required VramObservation Vram { get; init; }

    public required long? WorkingSetBytes { get; init; }
}
