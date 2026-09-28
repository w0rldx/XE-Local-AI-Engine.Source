namespace XE_Local_AI_Engine.Client.Services.Development;

internal sealed class DevelopmentCoderModelResult
{
    public required DevelopmentCoderSubmission Submission { get; init; }

    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }
}
