namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>Maps normalized V1 download values to the transport-independent image-model request.</summary>
internal static class StartImageModelDownloadRequestMapper
{
    public static ImageModelRequest ToServiceRequest(StartImageModelDownloadWireValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ImageModelRequest
        {
            ModelName = values.ModelName,
            RepoId = values.RepoId,
            Family = values.Family,
            Kind = values.Kind,
            Revision = values.Revision,
            Parts =
            [
                .. values.Parts.Select(static part => new ImageModelPartRequest
                {
                    Role = part.Role,
                    FileName = part.FileName,
                    Sha256 = part.Sha256,
                    RepoId = part.RepoId,
                    SizeBytes = part.SizeBytes
                })
            ]
        };
    }
}
