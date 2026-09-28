namespace XE_Local_AI_Engine.Client.Services.Development;

internal sealed class DevelopmentCloudAttemptContext
{
    public required DevelopmentCloudRoleRoute Route { get; init; }

    public required Guid ArtifactId { get; init; }
}
