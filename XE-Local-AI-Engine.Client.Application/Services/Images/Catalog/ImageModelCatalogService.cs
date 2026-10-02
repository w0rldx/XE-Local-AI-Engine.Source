namespace XE_Local_AI_Engine.Client.Services.Images.Catalog;

using XE_Local_AI_Engine.Client.Services.Images.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Joins the curated image-model catalog with the installed registry and this machine's measured memory budget, so each
///     entry carries its installed state and fit verdict.
/// </summary>
public sealed class ImageModelCatalogService
{
    private readonly IImageModelCatalog _catalog;
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly ILogger<ImageModelCatalogService> _logger;
    private readonly IImageModelRegistry _registry;

    public ImageModelCatalogService(IImageModelCatalog catalog,
        IImageModelRegistry registry,
        IHardwareProfiler hardwareProfiler,
        ILogger<ImageModelCatalogService> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hardwareProfiler);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(registry);
        _catalog = catalog;
        _hardwareProfiler = hardwareProfiler;
        _logger = logger;
        _registry = registry;
    }

    public async Task<ImageModelCatalogView> GetCatalogViewAsync(CancellationToken cancellationToken)
    {
        var document = _catalog.GetDocument();
        var installed = await _registry.ListAsync(cancellationToken);
        // Installed = the registry entry carries every role the catalog declares. A set installed before the catalog gained a part (Qwen-Image's
        // LlmVision) reads as not installed, so Install completes it: the store keeps the parts it has and fetches only the missing one.
        var installedRoles = installed.DistinctBy(static entry => entry.ModelName, StringComparer.OrdinalIgnoreCase)
                                      .ToDictionary(static entry => entry.ModelName,
                                          static entry => entry.Parts.Select(static part => part.Role.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase),
                                          StringComparer.OrdinalIgnoreCase);

        // A failed hardware probe must not fail the catalog: the list is still useful without a fit badge, and the
        // estimator reports Unknown for a null profile rather than guessing.
        HardwareProfile? profile = null;
        try
        {
            profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Hardware profiling failed while building the image model catalog; fit is reported as unknown.");
        }

        var entries = new List<ImageModelCatalogEntryView>(document.Models.Count);
        foreach (var entry in document.Models)
        {
            entries.Add(new ImageModelCatalogEntryView
            {
                Entry = entry,
                IsInstalled = installedRoles.TryGetValue(entry.Id, out var roles) && entry.Parts.All(part => roles.Contains(part.Role)),
                Fit = Estimate(entry, profile)
            });
        }

        return new ImageModelCatalogView
        {
            CatalogVersion = document.CatalogVersion,
            Entries = entries
        };
    }

    private static ImageModelFitEstimate Estimate(ImageModelCatalogEntry entry, HardwareProfile? profile)
    {
        // The catalog passed validation, so every role parses; a defensive fallback keeps a hypothetical future role
        // from throwing out of a read endpoint.
        var sizedParts = entry.Parts
                              .Select(part => new ImageModelPartSize(Enum.TryParse<ImageModelPartRole>(part.Role, ignoreCase: true, out var role)
                                      ? role
                                      : ImageModelPartRole.Diffusion,
                                  part.SizeBytes))
                              .ToList();

        return ImageModelFitEstimator.Estimate(sizedParts, profile);
    }
}
