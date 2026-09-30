namespace XE_Local_AI_Engine.Client.Services.Integrations;

using Microsoft.AspNetCore.Http.Metadata;

/// <summary>Endpoint metadata carrying the integration family's request-body cap, resolved once at composition.</summary>
internal sealed class IntegrationRequestSizeLimit : IRequestSizeLimitMetadata
{
    public IntegrationRequestSizeLimit(long maxRequestBodySize)
    {
        MaxRequestBodySize = maxRequestBodySize;
    }

    public long? MaxRequestBodySize { get; }
}
