namespace XE_Local_AI_Engine.Client.Services.Inference.Implementation;

internal sealed record VramObservation
{
    public required long? GlobalFreeBytes { get; init; }

    public required long? ProcessBudgetBytes { get; init; }

    public required long? ProcessBudgetExcessBytes { get; init; }

    public required double? ProcessBudgetExcessRatio { get; init; }

    public required long? PressureAboveBaselineBytes { get; init; }

    public required double? PressureAboveBaselineRatio { get; init; }

    public required bool ExternalPressureDetected { get; init; }

    public static VramObservation Create(long? globalFreeBytes, long? processBudgetBytes)
    {
        if (globalFreeBytes is not { } global || processBudgetBytes is not { } process || process <= global)
        {
            return new VramObservation
            {
                GlobalFreeBytes = globalFreeBytes,
                ProcessBudgetBytes = processBudgetBytes,
                ProcessBudgetExcessBytes = null,
                ProcessBudgetExcessRatio = null,
                PressureAboveBaselineBytes = null,
                PressureAboveBaselineRatio = null,
                ExternalPressureDetected = false
            };
        }

        var excess = process - global;
        var ratio = process > 0 ? (double)excess / process : 0d;
        return new VramObservation
        {
            GlobalFreeBytes = globalFreeBytes,
            ProcessBudgetBytes = processBudgetBytes,
            ProcessBudgetExcessBytes = excess,
            ProcessBudgetExcessRatio = ratio,
            PressureAboveBaselineBytes = null,
            PressureAboveBaselineRatio = null,
            ExternalPressureDetected = false
        };
    }
}
