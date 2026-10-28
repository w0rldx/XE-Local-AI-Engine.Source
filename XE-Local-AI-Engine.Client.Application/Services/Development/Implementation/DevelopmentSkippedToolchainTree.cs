namespace XE_Local_AI_Engine.Client.Services.Development.Implementation;

/// <summary>A tree that holds or leads to <see cref="Executable" /> on the host <c>PATH</c> and was not bound, with the rule that refused it.</summary>
internal sealed record DevelopmentSkippedToolchainTree
{
    public required string Executable { get; init; }

    public required string Tree { get; init; }

    public required string Reason { get; init; }
}
