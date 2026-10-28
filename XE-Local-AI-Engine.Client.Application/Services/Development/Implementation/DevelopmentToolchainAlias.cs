namespace XE_Local_AI_Engine.Client.Services.Development.Implementation;

/// <summary>A symlinked directory <see cref="LinkPath" /> the chain shows as a read-only bind of its canonical <see cref="Target" />.</summary>
internal sealed record DevelopmentToolchainAlias
{
    public required string LinkPath { get; init; }

    public required string Target { get; init; }
}
