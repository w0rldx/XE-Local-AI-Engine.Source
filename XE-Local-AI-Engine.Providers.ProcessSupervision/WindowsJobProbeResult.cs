namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>What <see cref="WindowsJobCeilingsProbe" /> measured.</summary>
public sealed record WindowsJobProbeResult
{
    /// <summary>Every control behaved: ceilings may be advertised on the plain Windows path.</summary>
    public required bool Supported { get; init; }

    /// <summary>Why they may not, when <see cref="Supported" /> is false.</summary>
    public string? Reason { get; init; }

    /// <summary>The CPU-rate denominator measured with the controls; meaningful only when <see cref="Supported" />.</summary>
    public int SystemProcessorCount { get; init; }
}
