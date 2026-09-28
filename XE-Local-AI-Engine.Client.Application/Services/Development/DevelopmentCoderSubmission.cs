namespace XE_Local_AI_Engine.Client.Services.Development;

internal sealed class DevelopmentCoderSubmission
{
    public required string Summary { get; init; }

    public required IReadOnlyList<string> ChangedFiles { get; init; }

    public required IReadOnlyList<string> CommandIds { get; init; }

    public required string? Notes { get; init; }
}
