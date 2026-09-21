namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Projects <see cref="INodePatchApplyService" />'s results onto the wire. A rename and nothing else: the service
///     already returns folder-relative paths and redacted rejection strings, so there is no host path for a mapper to
///     strip — and none for it to reintroduce.
/// </summary>
internal static class AgentHomePatchContractMapper
{
    public static AgentHomePatchPreviewResponse ToResponse(this NodePatchApplyPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        return new AgentHomePatchPreviewResponse
        {
            CanApply = preview.CanApply,
            Files = [.. preview.Files.Select(ToDto)],
            Rejections = [.. preview.Rejections.Select(ToDto)],
            DirtyTargets =
            [
                .. preview.DirtyTargets.Select(static entry => new AgentHomePatchDirtyTargetDto
                {
                    Path = entry.Path,
                    State = entry.State
                })
            ],
            DirtyCheckUnavailable = preview.DirtyCheckUnavailable,
            ContainsBinary = preview.ContainsBinary,
            PatchSha256 = preview.PatchSha256
        };
    }

    private static AgentHomePatchRejectionDto ToDto(PatchApplyRejection rejection)
    {
        return new AgentHomePatchRejectionDto
        {
            Reason = rejection.Reason,
            Path = rejection.Path
        };
    }

    public static AgentHomePatchApplyResponse ToResponse(this NodePatchApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AgentHomePatchApplyResponse
        {
            AppliedFiles = [.. result.AppliedFiles.Select(ToDto)]
        };
    }

    private static AgentHomePatchFileDto ToDto(PatchApplyFileEntry file)
    {
        return new AgentHomePatchFileDto
        {
            Alias = file.Alias,
            RelativePath = file.RelativePath,
            ChangeType = file.ChangeType,
            Added = file.Added,
            Removed = file.Removed
        };
    }
}
