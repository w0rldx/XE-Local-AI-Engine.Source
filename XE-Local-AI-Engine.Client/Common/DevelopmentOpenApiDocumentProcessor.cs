namespace XE_Local_AI_Engine.Client.Common;

using NSwag;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1;
using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     Keeps Development paths and schemas in workflow order without coupling the OpenAPI contract to compiler
///     source-file ordering. Unknown Development entries are retained after the known surface in their original order.
/// </summary>
internal sealed class DevelopmentOpenApiDocumentProcessor : IDocumentProcessor
{
    private const char UriPathSeparator = '/';
    private const string EndpointSchemaPrefix = "XE_Local_AI_EngineClientEndpointsDevelopmentV1";
    private const string ServiceSchemaPrefix = "XE_Local_AI_EngineClientServicesDevelopment";
    private const string PersistenceSchemaPrefix = "XE_Local_AI_EngineClientPersistenceDevelopment";
    private static readonly string DevelopmentRootPath = LocalPath(LocalApiRoutes.Development.Root);
    private static readonly string DevelopmentPathPrefix = DevelopmentRootPath + UriPathSeparator;
    private static readonly IReadOnlyDictionary<string, int> KnownPathRanks = CreateKnownPathRanks();
    private static readonly IReadOnlyDictionary<string, int> KnownSchemaRanks = CreateKnownSchemaRanks();

    public void Process(DocumentProcessorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        OrderPaths(context.Document);
        OrderSchemas(context.Document);
    }

    internal static void OrderPaths(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        OrderEntries(document.Paths, IsDevelopmentPath, KnownPathRanks);
    }

    internal static void OrderSchemas(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        OrderEntries(document.Components.Schemas, IsDevelopmentSchema, KnownSchemaRanks);
    }

    private static IReadOnlyDictionary<string, int> CreateKnownPathRanks()
    {
        string[] routes =
        [
            LocalApiRoutes.Development.Capability,
            LocalApiRoutes.Development.ContainerRuntimeConfirmation,
            LocalApiRoutes.Development.Repositories,
            LocalApiRoutes.Development.Templates,
            LocalApiRoutes.Development.TemplateById,
            LocalApiRoutes.Development.RepositoriesFromTemplate,
            LocalApiRoutes.Development.RepositoryProfileDetection,
            LocalApiRoutes.Development.Projects,
            LocalApiRoutes.Development.ProjectById,
            LocalApiRoutes.Development.TaskById,
            LocalApiRoutes.Development.NextAction,
            LocalApiRoutes.Development.CancelAttempt,
            LocalApiRoutes.Development.Events,
            LocalApiRoutes.Development.TaskArtifacts,
            LocalApiRoutes.Development.ArtifactById,
            LocalApiRoutes.Development.PatchPreview,
            LocalApiRoutes.Development.Apply,
            LocalApiRoutes.Development.RepositoryConnection
        ];

        return routes.Select(static (route, index) => new KeyValuePair<string, int>(LocalPath(route), index))
                     .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, int> CreateKnownSchemaRanks()
    {
        string[] schemas =
        [
            EndpointSchema(nameof(DevelopmentCapabilityResponse)),
            EndpointSchema(nameof(DevelopmentContainerRuntimeResponse)),
            EndpointSchema(nameof(DevelopmentContainerDaemonResponse)),
            EndpointSchema(nameof(SandboxIsolationSummaryResponse)),
            EndpointSchema(nameof(ConfirmDevelopmentContainerRuntimeRequest)),
            EndpointSchema(nameof(ListDevelopmentRepositoriesResponse)),
            EndpointSchema(nameof(DevelopmentRepositoryResponse)),
            EndpointSchema(nameof(RegisterDevelopmentRepositoryRequest)),
            EndpointSchema(nameof(ListDevelopmentTemplatesResponse)),
            EndpointSchema(nameof(DevelopmentTemplateResponse)),
            EndpointSchema(nameof(RegisterDevelopmentTemplateRequest)),
            EndpointSchema(nameof(DevelopmentTemplateRequest)),
            EndpointSchema(nameof(DevelopmentRepositoryFromTemplateResponse)),
            EndpointSchema(nameof(CreateDevelopmentRepositoryFromTemplateRequest)),
            EndpointSchema(nameof(DevelopmentProfileDetectionResponse)),
            EndpointSchema(nameof(DevelopmentProfileDetectionRequest)),
            EndpointSchema(nameof(ListDevelopmentProjectsResponse)),
            EndpointSchema(nameof(DevelopmentProjectResponse)),
            EndpointSchema(nameof(DevelopmentProjectDetailResponse)),
            EndpointSchema(nameof(DevelopmentTaskDetailResponse)),
            EndpointSchema(nameof(DevelopmentTaskResponse)),
            EndpointSchema(nameof(DevelopmentAttemptResponse)),
            EndpointSchema(nameof(DevelopmentArtifactResponse)),
            EndpointSchema(nameof(DevelopmentEventResponse)),
            EndpointSchema(nameof(CreateDevelopmentProjectRequest)),
            EndpointSchema(nameof(DevelopmentProjectRequest)),
            EndpointSchema(nameof(DevelopmentTaskRequest)),
            EndpointSchema(nameof(DevelopmentNextActionResponse)),
            EndpointSchema(nameof(DevelopmentActionRequest)),
            EndpointSchema(nameof(DevelopmentAttemptRequest)),
            EndpointSchema(nameof(ListDevelopmentEventsResponse)),
            EndpointSchema(nameof(ListDevelopmentArtifactsResponse)),
            EndpointSchema(nameof(DevelopmentArtifactContentResponse)),
            EndpointSchema(nameof(DevelopmentArtifactRequest)),
            EndpointSchema(nameof(DevelopmentPatchPreviewResponse)),
            ServiceSchema(nameof(DevelopmentPatchPreviewFile)),
            EndpointSchema(nameof(DevelopmentApplyResponse)),
            EndpointSchema(nameof(ReconnectDevelopmentRepositoryRequest))
        ];

        return schemas.Select(static (schema, index) => new KeyValuePair<string, int>(schema, index))
                      .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
    }

    private static void OrderEntries<TValue>(
        IDictionary<string, TValue> entries,
        Func<string, bool> isDevelopmentEntry,
        IReadOnlyDictionary<string, int> knownRanks)
    {
        var originalEntries = entries.ToArray();
        var developmentEntries = originalEntries.Select(static (entry, index) => (Entry: entry, Index: index))
                                                .Where(item => isDevelopmentEntry(item.Entry.Key))
                                                .OrderBy(item => knownRanks.GetValueOrDefault(item.Entry.Key, int.MaxValue))
                                                .ThenBy(static item => item.Index)
                                                .Select(static item => item.Entry)
                                                .ToArray();
        if (developmentEntries.Length < 2)
        {
            return;
        }

        var nextDevelopmentEntry = 0;
        entries.Clear();
        foreach (var entry in originalEntries)
        {
            var orderedEntry = isDevelopmentEntry(entry.Key)
                ? developmentEntries[nextDevelopmentEntry++]
                : entry;
            entries.Add(orderedEntry);
        }
    }

    private static bool IsDevelopmentPath(string path) =>
        string.Equals(path, DevelopmentRootPath, StringComparison.Ordinal)
        || path.StartsWith(DevelopmentPathPrefix, StringComparison.Ordinal);

    private static bool IsDevelopmentSchema(string schema) =>
        schema.StartsWith(EndpointSchemaPrefix, StringComparison.Ordinal)
        || schema.StartsWith(ServiceSchemaPrefix, StringComparison.Ordinal)
        || schema.StartsWith(PersistenceSchemaPrefix, StringComparison.Ordinal);

    private static string LocalPath(string route) => $"/{LocalApiRoutes.Prefix}/{route}";

    private static string EndpointSchema(string typeName) => EndpointSchemaPrefix + typeName;

    private static string ServiceSchema(string typeName) => ServiceSchemaPrefix + typeName;
}
