namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Extension methods that translate between the image endpoint DTOs and the coordinator / registry types. This is the
///     sole point in the Client project that references those member names — only this file needs adjustment if they
///     change. Enum values are surfaced as their string names so the wire contract is decoupled from the internal enum
///     types (a persistence/abstractions rename never silently changes the JSON form).
/// </summary>
internal static class ImageMapper
{
    // -----------------------------------------------------------------------
    // Create request → coordinator input
    // -----------------------------------------------------------------------

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
            CfgScale = request.CfgScale
        };
    }

    // -----------------------------------------------------------------------
    // Job view → response
    // -----------------------------------------------------------------------

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
            CancellationRequestedAtUtc = view.CancellationRequestedAtUtc
        };
    }

    // -----------------------------------------------------------------------
    // Installed model entry → response
    // -----------------------------------------------------------------------

    public static ImageModelResponse ToResponse(this ImageModelRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var defaults = ImageFamilyDefaults.For(entry.Family);
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
            DefaultSampler = defaults.Sampler
        };
    }
}
