namespace XE_Local_AI_Engine.Client.Common;

using NSwag;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>
///     Marks every External Apps operation <c>x-experimental: true</c> and describes its tag as experimental: the surface
///     is frozen outside the 1.0 API promise (ADR 0010 and 0011, amendment 2026-11-12).
/// </summary>
internal sealed class ExternalAppsExperimentalOpenApiDocumentProcessor : IDocumentProcessor
{
    internal const string ExtensionName = "x-experimental";
    internal const string TagName = "External-Apps";

    private const string PathPrefix = $"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.ExternalApps.Root}/";

    public void Process(DocumentProcessorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var document = context.Document;

        foreach (var operation in document.Paths
                                          .Where(static entry => entry.Key.StartsWith(PathPrefix, StringComparison.Ordinal))
                                          .SelectMany(static entry => entry.Value.Values))
        {
            operation.ExtensionData ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            operation.ExtensionData[ExtensionName] = true;
        }

        document.Tags.Add(new OpenApiTag
        {
            Name = TagName,
            Description = "Experimental: frozen and outside the 1.0 API promise; it may change or be removed without notice.",
            ExtensionData = new Dictionary<string, object?>(StringComparer.Ordinal) { [ExtensionName] = true }
        });
    }
}
