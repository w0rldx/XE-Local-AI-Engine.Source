namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;

/// <summary>Route-bound artifact identity, shared by the get/delete/cancel/license routes.</summary>
public sealed class BaseArtifactByIdRequest
{
    [RouteParam]
    public required Guid ArtifactId { get; init; }
}
