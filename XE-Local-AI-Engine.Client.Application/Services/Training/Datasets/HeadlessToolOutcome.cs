namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

public enum HeadlessToolOutcomeKind
{
    /// <summary>The real tool ran in-process — only ever for a ReadLocal tool whose composed approval is false.</summary>
    Executed,

    /// <summary>A statically verified mock answered.</summary>
    Mocked,

    /// <summary>Nothing ran: the call was validated but no real execution was permitted and no mock matched.</summary>
    ValidationOnly,

    /// <summary>The call could not be honored at all (unknown tool, unusable arguments, a throwing tool).</summary>
    Failed
}

public sealed class HeadlessToolOutcome
{
    public required HeadlessToolOutcomeKind Kind { get; init; }

    public required string? Result { get; init; }

    public required string Reason { get; init; }
}
