namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class DevelopmentPatchPreview
{
    public required DevelopmentApprovedApplySubject Subject { get; init; }

    public required string Patch { get; init; }

    public required IReadOnlyList<DevelopmentChangedFile> ChangedFiles { get; init; }
}
