namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>One registered <c>sd-server</c> daemon as the supervisor's process table holds it; no path, port or pid.</summary>
public sealed class ImageServerResidentSnapshot
{
    public required string ModelName { get; init; }

    /// <summary>Whether a generation holds a job lease on this daemon right now.</summary>
    public required bool HasActiveJobLease { get; init; }

    /// <summary>Whether the child exited on its own and has not been reaped from the table yet.</summary>
    public required bool HasExited { get; init; }

    public required DateTimeOffset LastUsedUtc { get; init; }
}
