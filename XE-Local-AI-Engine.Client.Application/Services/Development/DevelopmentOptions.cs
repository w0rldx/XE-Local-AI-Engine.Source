namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel.DataAnnotations;

public sealed class DevelopmentOptions
{
    public const string Section = "Development";

    public bool Enabled { get; init; }

    [Range(1, 256 * 1024 * 1024)]
    public int MaxArtifactBytes { get; init; } = 16 * 1024 * 1024;
}

