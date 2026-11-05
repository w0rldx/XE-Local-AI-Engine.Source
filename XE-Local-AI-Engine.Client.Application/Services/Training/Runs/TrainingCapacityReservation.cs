namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>A granted reservation, or a refusal carrying the reason. Disposing releases the reserved bytes.</summary>
public sealed class TrainingCapacityReservation : IDisposable
{
    public required bool Granted { get; init; }

    public required string? Reason { get; init; }

    public required IDisposable? Handle { get; init; }

    public void Dispose() =>
        Handle?.Dispose();
}
