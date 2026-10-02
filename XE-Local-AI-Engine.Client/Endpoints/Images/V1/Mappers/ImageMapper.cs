namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Images.Catalog;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Extension methods that translate between the image endpoint DTOs and the coordinator and registry types.
/// </summary>
/// <remarks>
///     The sole point in the Client project that references those member names, so only this file needs adjustment
///     when they change. Enum values are surfaced as their string names, decoupling the wire contract from the
///     internal enum types: a persistence or abstractions rename never silently changes the JSON form.
/// </remarks>
internal static class ImageMapper
{
    public static CreateImageJobInput ToInput(this CreateImageJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The wire seed is a precision-safe string; the endpoint has already validated it. A blank seed maps to the
        // coordinator's -1 random-seed sentinel; a parsed value carries through exactly.
        _ = SeedValue.TryParse(request.Seed, out var seed, out _);

        return new CreateImageJobInput
        {
            ModelName = request.ModelName,
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt,
            Seed = seed ?? -1,
            Width = request.Width,
            Height = request.Height,
            Steps = request.Steps,
            Sampler = request.Sampler,
            CfgScale = request.CfgScale,
            // The validator has already refused an unknown name.
            EditMode = ImageEditModeNames.TryParse(request.EditMode, out var mode) ? mode : null,
            SourceImageId = request.SourceImageId,
            Strength = request.Strength
        };
    }

    public static ImageJobResponse ToResponse(this ImageJobView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new ImageJobResponse
        {
            Id = view.Id,
            ModelName = view.ModelName,
            Prompt = view.Prompt,
            NegativePrompt = view.NegativePrompt,
            Seed = SeedValue.ToWire(view.Seed),
            Width = view.Width,
            Height = view.Height,
            Steps = view.Steps,
            Sampler = view.Sampler,
            CfgScale = view.CfgScale,
            Status = view.Status.ToString(),
            CreatedAtUtc = view.CreatedAtUtc,
            StartedAtUtc = view.StartedAtUtc,
            CompletedAtUtc = view.CompletedAtUtc,
            DurationMs = view.DurationMs,
            ImageId = view.ImageId,
            SanitizedError = view.SanitizedError,
            CancellationRequestedAtUtc = view.CancellationRequestedAtUtc,
            EditMode = view.EditMode is { } mode ? ImageEditModeNames.ToName(mode) : null,
            SourceImageId = view.SourceImageId,
            Strength = view.Strength
        };
    }

    public static ImageModelResponse ToResponse(this ImageModelRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var defaults = ImageFamilyDefaults.For(entry.Family);
        var editModes = ImageFamilyDefaults.EditModesFor(entry.Family, entry.Parts.Select(static p => p.Role));
        return new ImageModelResponse
        {
            ModelName = entry.ModelName,
            RepoId = entry.RepoId,
            Family = entry.Family.ToString(),
            Kind = entry.Kind.ToString(),
            SizeBytes = entry.SizeBytes,
            // LocalPath / Sha256 are deliberately omitted — never leak a filesystem path.
            Parts =
            [
                .. entry.Parts.Select(static p => new ImageModelPartResponse
                {
                    Role = p.Role.ToString(),
                    FileName = p.FileName,
                    SizeBytes = p.SizeBytes
                })
            ],
            DownloadedAtUtc = entry.DownloadedAtUtc.ToUnixTimeMilliseconds(),
            DefaultSteps = defaults.Steps,
            DefaultCfgScale = defaults.CfgScale,
            DefaultSampler = defaults.Sampler,
            EditModes = [.. editModes.Select(ImageEditModeNames.ToName)],
            NativePixels = defaults.NativePixels
        };
    }

    public static UploadedImageResponse ToUploadedResponse(this GeneratedImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new UploadedImageResponse
        {
            ImageId = info.ImageId,
            MimeType = info.MimeType,
            Width = info.Width,
            Height = info.Height,
            CreatedAtUtc = info.CreatedAtUtc
        };
    }

    public static UploadedImageResponse ToUploadedResponse(this GeneratedImageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new UploadedImageResponse
        {
            ImageId = row.ImageId,
            MimeType = row.MimeType,
            Width = row.Width,
            Height = row.Height,
            CreatedAtUtc = row.CreatedAtUtc
        };
    }

    public static GetImageModelCatalogResponse ToResponse(this ImageModelCatalogView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new GetImageModelCatalogResponse
        {
            CatalogVersion = view.CatalogVersion,
            Items = [.. view.Entries.Select(static entry => entry.ToResponse())]
        };
    }

    private static ImageModelCatalogEntryResponse ToResponse(this ImageModelCatalogEntryView view)
    {
        var entry = view.Entry;
        var fit = view.Fit;
        return new ImageModelCatalogEntryResponse
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            Publisher = entry.Publisher,
            RepoId = entry.RepoId,
            Family = entry.Family,
            License = entry.License,
            Recommended = entry.Recommended,
            Notes = entry.Notes,
            Parts =
            [
                .. entry.Parts.Select(static part => new ImageModelCatalogPartResponse
                {
                    Role = part.Role,
                    FileName = part.FileName,
                    RepoId = part.RepoId,
                    SizeBytes = part.SizeBytes
                })
            ],
            TotalSizeBytes = fit.TotalBytes,
            IsInstalled = view.IsInstalled,
            FitVerdict = fit.Verdict.ToString(),
            ResidentBytes = fit.ResidentBytes,
            FitBudgetBytes = fit.BudgetBytes,
            FitsOnDisk = fit.FitsOnDisk
        };
    }
}
