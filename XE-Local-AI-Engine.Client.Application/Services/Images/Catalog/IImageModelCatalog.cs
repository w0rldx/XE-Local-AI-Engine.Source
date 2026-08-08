namespace XE_Local_AI_Engine.Client.Services.Images.Catalog;

/// <summary>
///     Serves the curated image-model catalog — the list a user picks from instead of hand-typing a repo id, a weight
///     file name, a model name and a family. Bundled-only: unlike the GGUF catalog there is no remote refresh, because
///     the entries carry exact per-file sizes that were verified against the Hub before shipping and a stale remote
///     copy would silently reintroduce the 404-on-install failure the catalog exists to prevent.
/// </summary>
public interface IImageModelCatalog
{
    /// <summary>The validated catalog document. Never <see langword="null" />; an unusable bundle degrades to empty.</summary>
    ImageModelCatalogDocument GetDocument();
}
