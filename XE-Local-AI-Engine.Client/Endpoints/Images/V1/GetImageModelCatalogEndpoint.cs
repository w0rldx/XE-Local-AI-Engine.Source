namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images.Catalog;
using XE_Local_AI_Engine.Client.Services.Images.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     FastEndpoints handler for the curated image-model catalog (GET images/models/catalog). Joins three things the UI
///     would otherwise have to correlate itself: the bundled catalog entries, which of them are already installed, and
///     how each one's weights compare to this box's measured memory budget. Every entry carries its whole file-set in
///     the exact shape <c>POST images/models/downloads</c> accepts, so installing is one click and no typing.
///     Operator-gated; no path or token is surfaced.
/// </summary>
public sealed class GetImageModelCatalogEndpoint(
    IImageModelCatalog catalog,
    IImageModelRegistry registry,
    IHardwareProfiler hardwareProfiler,
    ILogger<GetImageModelCatalogEndpoint> logger)
    : EndpointWithoutRequest<GetImageModelCatalogResponse>
{
    private readonly IImageModelCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IHardwareProfiler _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
    private readonly ILogger<GetImageModelCatalogEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IImageModelRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.ModelCatalog);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var document = _catalog.GetDocument();
        var installed = await _registry.ListAsync(ct).ConfigureAwait(false);
        var installedNames = installed.Select(static entry => entry.ModelName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A failed hardware probe must not fail the catalog: the list is still useful without a fit badge, and the
        // estimator reports Unknown for a null profile rather than guessing.
        HardwareProfile? profile = null;
        try
        {
            profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Hardware profiling failed while building the image model catalog; fit is reported as unknown.");
        }

        var items = new List<ImageModelCatalogEntryResponse>(document.Models.Count);
        foreach (var entry in document.Models)
        {
            items.Add(ToResponse(entry, installedNames.Contains(entry.Id), profile));
        }

        await Send.OkAsync(new GetImageModelCatalogResponse
            {
                CatalogVersion = document.CatalogVersion,
                Items = items
            },
            ct).ConfigureAwait(false);
    }

    private static ImageModelCatalogEntryResponse ToResponse(ImageModelCatalogEntry entry, bool isInstalled, HardwareProfile? profile)
    {
        // The catalog passed validation, so every role parses; a defensive fallback keeps a hypothetical future role
        // from throwing out of a read endpoint.
        var sizedParts = entry.Parts
                              .Select(part => (Role: Enum.TryParse<ImageModelPartRole>(part.Role, ignoreCase: true, out var role)
                                      ? role
                                      : ImageModelPartRole.Diffusion,
                                  part.SizeBytes))
                              .ToList();

        var fit = ImageModelFitEstimator.Estimate(sizedParts, profile);

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
            IsInstalled = isInstalled,
            FitVerdict = fit.Verdict.ToString(),
            ResidentBytes = fit.ResidentBytes,
            FitBudgetBytes = fit.BudgetBytes,
            FitsOnDisk = fit.FitsOnDisk
        };
    }
}
