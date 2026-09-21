namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using Microsoft.AspNetCore.Http.Metadata;

/// <summary>Endpoint metadata raising this route's request-body cap to the configured archive cap.</summary>
internal sealed class SkillImportRequestSizeLimit : IRequestSizeLimitMetadata
{
    public SkillImportRequestSizeLimit(long maxRequestBodySize)
    {
        MaxRequestBodySize = maxRequestBodySize;
    }

    public long? MaxRequestBodySize { get; }
}
