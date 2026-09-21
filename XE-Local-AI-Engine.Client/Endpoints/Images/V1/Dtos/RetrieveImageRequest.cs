namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

/// <summary>Route-only request for <c>GET images/{imageId}</c>.</summary>
public sealed class RetrieveImageRequest
{
    /// <summary>Route-bound, server-generated image id (never a client-supplied path).</summary>
    public Guid ImageId { get; init; }
}
