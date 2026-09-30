namespace XE_Local_AI_Engine.Client.Hosting;

internal enum ReadyEvidenceState
{
    Absent,
    Valid,
    Invalid
}

internal sealed class ReadyEvidence
{
    public required ReadyEvidenceState State { get; init; }

    public required ReadyInfo? Info { get; init; }
}
