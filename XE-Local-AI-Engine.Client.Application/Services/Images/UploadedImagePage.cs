namespace XE_Local_AI_Engine.Client.Services.Images;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>One page of uploaded images plus the count of uploads that exist in total, ignoring paging.</summary>
public sealed class UploadedImagePage
{
    public required IReadOnlyList<GeneratedImageRow> Items { get; init; }

    public required int TotalCount { get; init; }
}
