namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class OpenApiDocumentTests
{
    private static readonly string[] HttpVerbs = ["get", "post", "put", "delete", "patch", "options", "head"];

    // operationIds drive the generated hey-api React SDK function names. They must be clean, lower-camelCase,
    // and namespace-free — never the FastEndpoints default (e.g. "xeLocalAiEngineClientEndpoints...Endpoint").
    private static readonly Regex CleanCamelCase = new("^[a-z][A-Za-z0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Test]
    public async Task LocalOpenApiDocument_DescribesLocalApiOnly()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/local/v1/v1.json").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);
        var paths = document.RootElement.GetProperty("paths");

        AssertEx.True(paths.TryGetProperty("/api/local/v1/diagnostics/validation-probe", out _),
            "Expected the node-local validation probe endpoint in the OpenAPI document.");
        AssertEx.False(paths.TryGetProperty("/api/v1/schedule", out _),
            "The node OpenAPI document must not include platform API routes.");
    }

    [Test]
    public async Task LocalOpenApiDocument_HasNoDuplicateOperationIds()
    {
        var operationIds = await GetOperationIdsAsync().ConfigureAwait(false);

        AssertEx.True(operationIds.Count > 0, "Expected the OpenAPI document to expose at least one operation.");

        var duplicates = operationIds
                         .GroupBy(static id => id, StringComparer.Ordinal)
                         .Where(static group => group.Count() > 1)
                         .Select(static group => group.Key)
                         .ToList();

        AssertEx.True(duplicates.Count == 0,
            $"OpenAPI operationIds must be globally unique (NSwag emits one operationId namespace). Duplicates: {string.Join(", ", duplicates)}");
    }

    [Test]
    public async Task LocalOpenApiDocument_AllOperationsHaveCleanCamelCaseNames()
    {
        var operationIds = await GetOperationIdsAsync().ConfigureAwait(false);

        AssertEx.True(operationIds.Count > 0, "Expected the OpenAPI document to expose at least one operation.");

        // The global Endpoints.NameGenerator (Program.cs) strips the "Endpoint" suffix and lower-cases the first
        // character, so every operationId must match clean camelCase and never fall back to the namespaced default.
        var offenders = operationIds.Where(static id => !CleanCamelCase.IsMatch(id)).ToList();

        AssertEx.True(offenders.Count == 0,
            $"Every operationId must be clean lower-camelCase (no namespaced FastEndpoints default). Offenders: {string.Join(", ", offenders)}");

        // Spot-check a representative generated SDK name to guard against an empty/over-broad match above.
        AssertEx.Contains(operationIds, "createScheduledJob");
    }

    [Test]
    public async Task LocalOpenApiDocument_DescribesGeneralizedAndLegacySourceBuildSurfaces()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json").ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);
        var paths = document.RootElement.GetProperty("paths");

        foreach (var path in new[]
                 {
                     "/api/local/v1/model-fit/llamacpp/source-build",
                     "/api/local/v1/model-fit/llamacpp/source-build/prerequisites",
                     "/api/local/v1/model-fit/llamacpp/source-build/status",
                     "/api/local/v1/model-fit/llamacpp/source-build/cancel",
                     "/api/local/v1/model-fit/llamacpp/source-build/remove",
                     "/api/local/v1/model-fit/llamacpp/cuda-build"
                 })
        {
            AssertEx.True(paths.TryGetProperty(path, out _), $"Expected source-build path '{path}'.");
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        AssertSchemaEnum(schemas, "LlamaCppSourceBackendDto", ["cpu", "vulkan", "cuda"]);
        AssertSchemaEnum(schemas, "LlamaCppSourceSelectionDto", ["official", "custom"]);
        AssertSchemaEnum(schemas, "LlamaCppSourceRevisionModeDto", ["enginePinned", "defaultBranch", "explicitCommit"]);

        AssertResponses(paths, "/api/local/v1/model-fit/llamacpp/source-build", "post", ["200", "400", "409"]);
        AssertResponses(paths, "/api/local/v1/model-fit/llamacpp/source-build/prerequisites", "get", ["200", "400"]);
        AssertResponses(paths, "/api/local/v1/model-fit/llamacpp/source-build/status", "get", ["200"]);
        AssertResponses(paths, "/api/local/v1/model-fit/llamacpp/source-build/cancel", "post", ["200"]);
        AssertResponses(paths, "/api/local/v1/model-fit/llamacpp/source-build/remove", "post", ["200", "409"]);
        AssertResponses(paths, "/api/local/v1/model-fit/llamacpp/version", "post", ["200", "400", "409"]);
        AssertResponses(paths, "/api/local/v1/model-fit/llamacpp/update", "post", ["200", "400", "409"]);

        var requestSchema = FindSchema(schemas, "StartLlamaCppSourceBuildRequest");
        AssertEx.True(requestSchema.GetProperty("required").EnumerateArray()
                                   .Any(static property => property.GetString() == "acknowledgeCustomSourceRisk"),
            "The custom-source risk acknowledgement must be required on the wire.");
        AssertSchemaProperties(schemas, "LlamaCppSourceBuildDescriptorResponse",
            ["buildId", "backend", "source", "repository", "revisionMode", "requestedCommit", "resolvedCommit"]);
        AssertSchemaProperties(schemas, "LlamaCppInstalledRuntimeResponse",
            ["sourceRepository", "sourceCommit", "sourceSelection", "sourceRevisionMode", "sourceRequestedCommit"]);
    }

    [Test]
    public async Task LocalOpenApiDocument_DescribesImageRuntimeSourceBuildSurface()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json").ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);
        var paths = document.RootElement.GetProperty("paths");

        foreach (var path in new[]
                 {
                     "/api/local/v1/images/runtime",
                     "/api/local/v1/images/runtime/eject",
                     "/api/local/v1/images/runtime/source-build",
                     "/api/local/v1/images/runtime/source-build/prerequisites",
                     "/api/local/v1/images/runtime/source-build/status",
                     "/api/local/v1/images/runtime/source-build/cancel",
                     "/api/local/v1/images/runtime/source-build/remove"
                 })
        {
            AssertEx.True(paths.TryGetProperty(path, out _), $"Expected image-runtime path '{path}'.");
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        AssertSchemaEnum(schemas, "StableDiffusionCppSourceBackendDto", ["cpu", "vulkan", "cuda"]);
        AssertSchemaEnum(schemas, "StableDiffusionCppSourceSelectionDto", ["official", "custom"]);
        AssertSchemaEnum(schemas, "StableDiffusionCppSourceRevisionModeDto", ["enginePinned", "defaultBranch", "explicitCommit"]);
        AssertSchemaEnum(schemas, "StableDiffusionInstalledRuntimeValidityDto", ["active", "invalid"]);

        AssertResponses(paths, "/api/local/v1/images/runtime", "get", ["200"]);
        AssertResponses(paths, "/api/local/v1/images/runtime/eject", "post", ["200", "409"]);
        AssertResponses(paths, "/api/local/v1/images/runtime/source-build", "post", ["200", "400", "409"]);
        AssertResponses(paths, "/api/local/v1/images/runtime/source-build/prerequisites", "get", ["200", "400"]);
        AssertResponses(paths, "/api/local/v1/images/runtime/source-build/status", "get", ["200"]);
        AssertResponses(paths, "/api/local/v1/images/runtime/source-build/cancel", "post", ["200"]);
        AssertResponses(paths, "/api/local/v1/images/runtime/source-build/remove", "post", ["200", "409"]);
        AssertResponses(paths, "/api/local/v1/images/jobs", "post", ["200", "400", "409"]);

        foreach (var path in new[]
                 {
                     "/api/local/v1/images/runtime/eject",
                     "/api/local/v1/images/runtime/source-build/cancel",
                     "/api/local/v1/images/runtime/source-build/remove"
                 })
        {
            var content = paths.GetProperty(path).GetProperty("post").GetProperty("requestBody").GetProperty("content");
            AssertEx.True(content.TryGetProperty("application/json", out _),
                $"Expected generated transport metadata for '{path}' to accept an explicit empty JSON action body.");
        }

        var requestSchema = FindSchema(schemas, "StartStableDiffusionCppSourceBuildRequest");
        AssertEx.True(requestSchema.GetProperty("required").EnumerateArray()
                                   .Any(static property => property.GetString() == "acknowledgeCustomSourceRisk"),
            "The stable-diffusion.cpp custom-source risk acknowledgement must be required on the wire.");
        AssertSchemaProperties(schemas, "StableDiffusionCppSourceBuildDescriptorResponse",
            ["buildId", "backend", "source", "repository", "revisionMode", "requestedCommit", "resolvedCommit"]);
        AssertSchemaProperties(schemas, "StableDiffusionInstalledRuntimeResponse",
        [
            "validity", "desiredBackend", "sourceRepository", "sourceCommit", "sourceSelection", "sourceRevisionMode",
            "sourceRequestedCommit", "installedAtUtc", "invalidReason"
        ]);
        AssertSchemaProperties(schemas, "ImageRuntimeActivityResponse",
            ["activeJobCount", "spawnReadinessCount", "residentProcessCount", "mutationReserved", "evictionReserved", "isBusy"]);
        AssertSchemaProperties(schemas, "ImageRuntimeStatusResponse", ["managedRuntime", "activity"]);
        AssertSchemaProperties(schemas, "ImageRuntimeBlockedResponse", ["reason", "message", "activity"]);
    }

    private static void AssertResponses(JsonElement paths, string path, string verb, IReadOnlyList<string> expected)
    {
        var responses = paths.GetProperty(path).GetProperty(verb).GetProperty("responses");
        foreach (var status in expected)
        {
            AssertEx.True(responses.TryGetProperty(status, out _), $"Expected {verb.ToUpperInvariant()} {path} to document {status}.");
        }
    }

    private static JsonElement FindSchema(JsonElement schemas, string schemaSuffix)
    {
        return schemas.EnumerateObject().Single(property => property.Name.EndsWith(schemaSuffix, StringComparison.Ordinal)).Value;
    }

    private static void AssertSchemaProperties(JsonElement schemas, string schemaSuffix, IReadOnlyList<string> expected)
    {
        var properties = FindSchema(schemas, schemaSuffix).GetProperty("properties");
        foreach (var property in expected)
        {
            AssertEx.True(properties.TryGetProperty(property, out _), $"Expected {schemaSuffix}.{property} in OpenAPI.");
        }
    }

    private static void AssertSchemaEnum(JsonElement schemas, string schemaSuffix, IReadOnlyList<string> expected)
    {
        var schema = FindSchema(schemas, schemaSuffix);
        var values = schema.GetProperty("enum").EnumerateArray().Select(static value => value.GetString() ?? string.Empty).ToArray();
        AssertEx.Equal(string.Join('|', expected), string.Join('|', values));
    }

    private static async Task<List<string>> GetOperationIdsAsync()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/local/v1/v1.json").ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);

        var operationIds = new List<string>();
        foreach (var pathItem in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in pathItem.Value.EnumerateObject())
            {
                if (!HttpVerbs.Contains(operation.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (operation.Value.TryGetProperty("operationId", out var operationId)
                    && operationId.GetString() is { Length: > 0 } value)
                {
                    operationIds.Add(value);
                }
                else
                {
                    operationIds.Add($"(MISSING:{operation.Name} {pathItem.Name})");
                }
            }
        }

        return operationIds;
    }
}
