namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using NJsonSchema;
using NSwag;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentOpenApiDocumentProcessorTests
{
    private const string EndpointSchemaPrefix = "XE_Local_AI_EngineClientEndpointsDevelopmentV1";
    private const string ServiceSchemaPrefix = "XE_Local_AI_EngineClientServicesDevelopment";
    private static readonly string[] ExpectedDevelopmentPaths =
    [
        "/api/local/v1/development/capability",
        "/api/local/v1/development/container-runtime/confirmation",
        "/api/local/v1/development/repositories",
        "/api/local/v1/development/templates",
        "/api/local/v1/development/templates/{templateId}",
        "/api/local/v1/development/repositories/from-template",
        "/api/local/v1/development/repositories/{selectedFolderId}/profile-detection",
        "/api/local/v1/development/projects",
        "/api/local/v1/development/projects/{projectId}",
        "/api/local/v1/development/projects/{projectId}/tasks/{taskId}",
        "/api/local/v1/development/projects/{projectId}/tasks/{taskId}/next-action",
        "/api/local/v1/development/projects/{projectId}/tasks/{taskId}/attempts/{attemptId}/cancel",
        "/api/local/v1/development/projects/{projectId}/events",
        "/api/local/v1/development/projects/{projectId}/tasks/{taskId}/artifacts",
        "/api/local/v1/development/projects/{projectId}/tasks/{taskId}/artifacts/{artifactId}",
        "/api/local/v1/development/projects/{projectId}/tasks/{taskId}/preview",
        "/api/local/v1/development/projects/{projectId}/tasks/{taskId}/apply",
        "/api/local/v1/development/projects/{projectId}/repository-connection"
    ];

    private static readonly string[] ExpectedDevelopmentSchemas =
    [
        EndpointSchema("DevelopmentCapabilityResponse"),
        EndpointSchema("DevelopmentContainerRuntimeResponse"),
        EndpointSchema("DevelopmentContainerDaemonResponse"),
        EndpointSchema("SandboxIsolationSummaryResponse"),
        EndpointSchema("ConfirmDevelopmentContainerRuntimeRequest"),
        EndpointSchema("ListDevelopmentRepositoriesResponse"),
        EndpointSchema("DevelopmentRepositoryResponse"),
        EndpointSchema("RegisterDevelopmentRepositoryRequest"),
        EndpointSchema("ListDevelopmentTemplatesResponse"),
        EndpointSchema("DevelopmentTemplateResponse"),
        EndpointSchema("RegisterDevelopmentTemplateRequest"),
        EndpointSchema("DevelopmentTemplateRequest"),
        EndpointSchema("DevelopmentRepositoryFromTemplateResponse"),
        EndpointSchema("CreateDevelopmentRepositoryFromTemplateRequest"),
        EndpointSchema("DevelopmentProfileDetectionResponse"),
        EndpointSchema("DevelopmentProfileDetectionRequest"),
        EndpointSchema("ListDevelopmentProjectsResponse"),
        EndpointSchema("DevelopmentProjectResponse"),
        EndpointSchema("DevelopmentProjectDetailResponse"),
        EndpointSchema("DevelopmentTaskDetailResponse"),
        EndpointSchema("DevelopmentTaskResponse"),
        EndpointSchema("DevelopmentAttemptResponse"),
        EndpointSchema("DevelopmentArtifactResponse"),
        EndpointSchema("DevelopmentEventResponse"),
        EndpointSchema("CreateDevelopmentProjectRequest"),
        EndpointSchema("DevelopmentProjectRequest"),
        EndpointSchema("DevelopmentTaskRequest"),
        EndpointSchema("DevelopmentNextActionResponse"),
        EndpointSchema("DevelopmentActionRequest"),
        EndpointSchema("DevelopmentAttemptRequest"),
        EndpointSchema("ListDevelopmentEventsResponse"),
        EndpointSchema("ListDevelopmentArtifactsResponse"),
        EndpointSchema("DevelopmentArtifactContentResponse"),
        EndpointSchema("DevelopmentArtifactRequest"),
        EndpointSchema("DevelopmentPatchPreviewResponse"),
        ServiceSchema("DevelopmentPatchPreviewFile"),
        EndpointSchema("DevelopmentApplyResponse"),
        EndpointSchema("ReconnectDevelopmentRepositoryRequest")
    ];

    [Test]
    public void OrderPaths_SortsTheKnownDevelopmentSurfaceWithoutMovingOtherPaths()
    {
        var document = Document(
        [
            "/health/live",
            .. ExpectedDevelopmentPaths.Reverse(),
            "/api/local/v1/models"
        ]);

        DevelopmentOpenApiDocumentProcessor.OrderPaths(document);

        AssertEx.Equal(string.Join('|', ["/health/live", .. ExpectedDevelopmentPaths, "/api/local/v1/models"]),
            string.Join('|', document.Paths.Keys));
    }

    [Test]
    public void OrderPaths_RetainsUnknownDevelopmentPathsAndNonDevelopmentPositions()
    {
        const string unknownFirst = "/api/local/v1/development/future/first";
        const string unknownSecond = "/api/local/v1/development/future/second";
        var document = Document(
        [
            "/health/live",
            unknownFirst,
            ExpectedDevelopmentPaths[^1],
            "/api/local/v1/models",
            unknownSecond,
            ExpectedDevelopmentPaths[0],
            "/openapi/local/v1/v1.json"
        ]);

        DevelopmentOpenApiDocumentProcessor.OrderPaths(document);

        AssertEx.Equal(string.Join('|',
                "/health/live",
                ExpectedDevelopmentPaths[0],
                ExpectedDevelopmentPaths[^1],
                "/api/local/v1/models",
                unknownFirst,
                unknownSecond,
                "/openapi/local/v1/v1.json"),
            string.Join('|', document.Paths.Keys));
    }

    [Test]
    public void OrderSchemas_SortsTheKnownDevelopmentSchemasWithoutMovingOtherSchemas()
    {
        var document = Document([]);
        AddSchemas(document,
        [
            "UnrelatedBefore",
            .. ExpectedDevelopmentSchemas.Reverse(),
            "UnrelatedAfter"
        ]);

        DevelopmentOpenApiDocumentProcessor.OrderSchemas(document);

        AssertEx.Equal(string.Join('|', ["UnrelatedBefore", .. ExpectedDevelopmentSchemas, "UnrelatedAfter"]),
            string.Join('|', document.Components.Schemas.Keys));
    }

    [Test]
    public void OrderSchemas_RetainsUnknownDevelopmentSchemasAndNonDevelopmentPositions()
    {
        const string unknownEndpoint = EndpointSchemaPrefix + "FutureResponse";
        const string unknownService = ServiceSchemaPrefix + "FutureModel";
        var document = Document([]);
        AddSchemas(document,
        [
            "UnrelatedBefore",
            unknownEndpoint,
            ExpectedDevelopmentSchemas[^1],
            "UnrelatedMiddle",
            unknownService,
            ExpectedDevelopmentSchemas[0],
            "UnrelatedAfter"
        ]);

        DevelopmentOpenApiDocumentProcessor.OrderSchemas(document);

        AssertEx.Equal(string.Join('|',
                "UnrelatedBefore",
                ExpectedDevelopmentSchemas[0],
                ExpectedDevelopmentSchemas[^1],
                "UnrelatedMiddle",
                unknownEndpoint,
                unknownService,
                "UnrelatedAfter"),
            string.Join('|', document.Components.Schemas.Keys));
    }

    private static OpenApiDocument Document(IEnumerable<string> paths)
    {
        var document = new OpenApiDocument();
        foreach (var path in paths)
        {
            document.Paths.Add(path, new OpenApiPathItem());
        }

        return document;
    }

    private static void AddSchemas(OpenApiDocument document, IEnumerable<string> schemas)
    {
        foreach (var schema in schemas)
        {
            document.Components.Schemas.Add(schema, new JsonSchema());
        }
    }

    private static string EndpointSchema(string typeName) => EndpointSchemaPrefix + typeName;

    private static string ServiceSchema(string typeName) => ServiceSchemaPrefix + typeName;
}
