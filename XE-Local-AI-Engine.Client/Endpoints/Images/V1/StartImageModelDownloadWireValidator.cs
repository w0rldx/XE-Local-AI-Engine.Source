namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>Validates and normalizes the V1 download wire contract before it crosses into image services.</summary>
internal static class StartImageModelDownloadWireValidator
{
    public static StartImageModelDownloadWireValidationResult Validate(StartImageModelDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ModelName))
        {
            return Invalid("A model name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            return Invalid("A repository id is required.");
        }

        if (!Enum.TryParse<ImageModelFamily>(request.Family, ignoreCase: true, out var family) || family == ImageModelFamily.Unknown)
        {
            return Invalid("A valid model family is required.");
        }

        var kind = ImageModelKind.Txt2Img;
        if (!string.IsNullOrWhiteSpace(request.Kind) && !Enum.TryParse(request.Kind, ignoreCase: true, out kind))
        {
            return Invalid("The model kind is not recognized.");
        }

        if (request.Parts is null || request.Parts.Count == 0)
        {
            return Invalid("At least one weight part is required.");
        }

        var parts = new List<StartImageModelDownloadPartWireValues>(request.Parts.Count);
        foreach (var part in request.Parts)
        {
            if (!Enum.TryParse<ImageModelPartRole>(part.Role, ignoreCase: true, out var role))
            {
                return Invalid($"The part role '{part.Role}' is not recognized.");
            }

            if (string.IsNullOrWhiteSpace(part.FileName))
            {
                return Invalid("Each weight part requires a file name.");
            }

            parts.Add(new StartImageModelDownloadPartWireValues(role,
                part.FileName.Trim(),
                NormalizeOptional(part.Sha256),
                NormalizeOptional(part.RepoId),
                part.SizeBytes is > 0 ? part.SizeBytes : null));
        }

        return new StartImageModelDownloadWireValidationResult(new StartImageModelDownloadWireValues(request.ModelName.Trim(),
                request.RepoId.Trim(),
                family,
                kind,
                NormalizeOptional(request.Revision),
                parts),
            Error: null);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StartImageModelDownloadWireValidationResult Invalid(string error) =>
        new(Values: null, error);
}

internal sealed record StartImageModelDownloadWireValidationResult(StartImageModelDownloadWireValues? Values, string? Error)
{
    public bool IsValid => Values is not null;
}

internal sealed record StartImageModelDownloadWireValues(
    string ModelName,
    string RepoId,
    ImageModelFamily Family,
    ImageModelKind Kind,
    string? Revision,
    IReadOnlyList<StartImageModelDownloadPartWireValues> Parts);

internal sealed record StartImageModelDownloadPartWireValues(
    ImageModelPartRole Role,
    string FileName,
    string? Sha256,
    string? RepoId,
    long? SizeBytes);
