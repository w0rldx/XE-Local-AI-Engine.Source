namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

internal static class BaseArtifactEndpointMapper
{
    public static BaseArtifactResponse ToResponse(this BaseArtifactView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new BaseArtifactResponse
        {
            Id = view.Id,
            RepoId = view.RepoId,
            Revision = view.Revision,
            Status = view.Status,
            TotalBytes = view.TotalBytes,
            Files = view.Files.Select(ToResponse).ToArray(),
            License = view.License?.ToResponse(),
            ErrorMessage = view.ErrorMessage,
            Version = view.Version,
            CreatedAtUtc = view.CreatedAtUtc.ToUnixTimeMilliseconds(),
            UpdatedAtUtc = view.UpdatedAtUtc.ToUnixTimeMilliseconds(),
            Progress = view.Progress is null
                ? null
                : new BaseArtifactProgressResponse
                {
                    CompletedBytes = view.Progress.CompletedBytes,
                    TotalBytes = view.Progress.TotalBytes,
                    FileIndex = view.Progress.FileIndex,
                    FileCount = view.Progress.FileCount
                }
        };
    }

    public static BaseArtifactLicenseResponse ToResponse(this BaseArtifactLicenseView license)
    {
        ArgumentNullException.ThrowIfNull(license);

        return new BaseArtifactLicenseResponse
        {
            RepoId = license.RepoId,
            License = license.License,
            IsGated = license.IsGated,
            FetchedAtUtc = license.FetchedAtUtc.ToUnixTimeMilliseconds()
        };
    }

    // LocalPath is deliberately absent from the wire shape: it is an absolute on-disk path, and the security posture
    // keeps those out of transport and logs.
    private static BaseArtifactFileResponse ToResponse(BaseArtifactFileView file)
    {
        return new BaseArtifactFileResponse
        {
            Role = file.Role,
            FileName = file.FileName,
            SizeBytes = file.SizeBytes,
            Sha256 = file.Sha256
        };
    }
}
