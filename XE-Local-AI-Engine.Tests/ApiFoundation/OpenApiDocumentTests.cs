namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class OpenApiDocumentTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    private static readonly string[] HttpVerbs = ["get", "post", "put", "delete", "patch", "options", "head"];

    // The whole work-session REST surface, asserted twice: once on the shared fixture and once on a node with the
    // feature switched off. Kept in one place so the two runs cannot drift apart.
    private static readonly (string Path, string[] Verbs)[] WorkSessionPaths =
    [
        ("/api/local/v1/work-sessions", ["get", "post"]),
        // The one path that also answers on a node with the feature off; it is in the document on every node like the rest.
        ("/api/local/v1/work-sessions/capability", ["get"]),
        ("/api/local/v1/work-sessions/{sessionId}", ["get", "patch", "delete"]),
        ("/api/local/v1/work-sessions/{sessionId}/start", ["post"]),
        ("/api/local/v1/work-sessions/{sessionId}/pause", ["post"]),
        ("/api/local/v1/work-sessions/{sessionId}/resume", ["post"]),
        ("/api/local/v1/work-sessions/{sessionId}/cancel", ["post"]),
        ("/api/local/v1/work-sessions/{sessionId}/tasks", ["get"]),
        ("/api/local/v1/work-sessions/{sessionId}/findings", ["get"]),
        ("/api/local/v1/work-sessions/{sessionId}/artifacts", ["get"]),
        ("/api/local/v1/work-sessions/{sessionId}/checkpoints", ["get"]),
        ("/api/local/v1/work-sessions/{sessionId}/events", ["get"]),
        ("/api/local/v1/work-sessions/{sessionId}/artifacts/{artifactId}/content", ["get"]),
        ("/api/local/v1/work-sessions/{sessionId}/messages", ["post"])
    ];

    // The whole external-apps REST surface: 18 distinct paths carrying 20 operations. Both numbers are DERIVED from
    // this table below, so neither can be asserted wrong, and the table is what proves the generated client does not
    // depend on whether the node that produced the spec had the feature on.
    private static readonly (string Path, string[] Verbs)[] ExternalAppPaths =
    [
        ("/api/local/v1/external-apps/runtime", ["get"]),
        ("/api/local/v1/external-apps/runtime/refresh", ["post"]),
        ("/api/local/v1/external-apps/catalog", ["get"]),
        ("/api/local/v1/external-apps/catalog/refresh", ["post"]),
        ("/api/local/v1/external-apps/catalog/{applicationId}", ["get"]),
        ("/api/local/v1/external-apps/catalog/{applicationId}/install-preview", ["get"]),
        ("/api/local/v1/external-apps/instances", ["get", "post"]),
        ("/api/local/v1/external-apps/instances/{instanceId}", ["get", "delete"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/update-preview", ["get"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/start", ["post"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/stop", ["post"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/restart", ["post"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/reset", ["post"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/update", ["post"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/cancel", ["post"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/variables", ["put"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/events", ["get"]),
        ("/api/local/v1/external-apps/instances/{instanceId}/logs", ["get"])
    ];

    // operationIds drive the generated hey-api React SDK function names. They must be clean, lower-camelCase,
    // and namespace-free — never the FastEndpoints default (e.g. "xeLocalAiEngineClientEndpoints...Endpoint").
    private static readonly Regex CleanCamelCase = new("^[a-z][A-Za-z0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The deletes that genuinely read a payload: an optimistic-concurrency version everywhere but the chat purge,
    // which sends a flag. Every other GET or DELETE declares no body. Each entry is asserted to still be one.
    private static readonly string[] DeleteOperationsThatReadABody =
    [
        "clearBenchmarkRunScore", "deleteBenchmarkProject", "deleteBenchmarkRun", "deleteBenchmarkTaskItem",
        "deleteComparison", "deleteEvaluation", "deleteNodeChatConversation", "deleteToolMock",
        "deleteTrainingArtifact", "deleteTrainingDataset", "deleteTrainingDefinition"
    ];

    [Test]
    public async Task LocalOpenApiDocument_DescribesLocalApiOnly()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/local/v1/v1.json");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var paths = document.RootElement.GetProperty("paths");

        AssertEx.True(paths.TryGetProperty("/api/local/v1/diagnostics/validation-probe", out _),
            "Expected the node-local validation probe endpoint in the OpenAPI document.");
        AssertEx.False(paths.TryGetProperty("/api/v1/schedule", out _),
            "The node OpenAPI document must not include platform API routes.");
    }

    [Test]
    public async Task LocalOpenApiDocument_NamesEverySchemaPropertyAndParameterInCamelCase()
    {
        // The general form of the invariant every named-DTO assertion in this class depends on. The document is
        // generated against a COPY of the FastEndpoints serializer global, taken when NSwag resolves the document
        // registration — which UseOpenApi does while the pipeline is being built, before UseFastEndpoints populates
        // that global. ConfigureServices seeds it at registration time so the snapshot cannot be the bare PascalCase
        // default; without that seeding this document describes a camelCase API with PascalCase names, and because
        // the global is process-wide, whether it did so depended on which host booted first in the test process.
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);

        var propertyNames = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject()
                                    .Where(static schema => schema.Value.TryGetProperty("properties", out _))
                                    .SelectMany(static schema => schema.Value.GetProperty("properties").EnumerateObject()
                                                                       .Select(property => $"{schema.Name}.{property.Name}"))
                                    .ToList();
        var parameterNames = document.RootElement.GetProperty("paths").EnumerateObject()
                                     .SelectMany(static path => path.Value.EnumerateObject()
                                                                    .Where(static operation => operation.Value.TryGetProperty("parameters", out _))
                                                                    .SelectMany(operation => operation.Value.GetProperty("parameters").EnumerateArray()
                                                                                                      .Select(parameter => $"{path.Name}:{parameter.GetProperty("name").GetString()}")))
                                     .ToList();

        // Non-vacuity floors: a document that generated nothing would otherwise pass this with zero offenders.
        AssertEx.True(propertyNames.Count >= 1000,
            $"Expected the OpenAPI document to describe at least 1000 schema properties; found {propertyNames.Count}. Refusing a vacuous casing pass.");
        AssertEx.True(parameterNames.Count >= 50,
            $"Expected the OpenAPI document to describe at least 50 operation parameters; found {parameterNames.Count}. Refusing a vacuous casing pass.");

        var offenders = propertyNames.Concat(parameterNames)
                                     .Where(static name => char.IsUpper(name[(name.LastIndexOfAny(['.', ':']) + 1)..][0]))
                                     .Order(StringComparer.Ordinal)
                                     .ToList();

        AssertEx.True(offenders.Count == 0,
            $"Every OpenAPI schema property and operation parameter must be camelCase — the wire format the node actually serializes. "
            + $"{offenders.Count} of {propertyNames.Count + parameterNames.Count} are not: {string.Join(", ", offenders.Take(20))}"
            + (offenders.Count > 20 ? ", …" : string.Empty));
    }

    [Test]
    public async Task LocalOpenApiDocument_HasNoDuplicateOperationIds()
    {
        var operationIds = await GetOperationIdsAsync();

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
        var operationIds = await GetOperationIdsAsync();

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
    public async Task LocalOpenApiDocument_DescribesTheGeneralizedSourceBuildSurface()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var paths = document.RootElement.GetProperty("paths");

        foreach (var path in new[]
                 {
                     "/api/local/v1/model-fit/llamacpp/source-build",
                     "/api/local/v1/model-fit/llamacpp/source-build/prerequisites",
                     "/api/local/v1/model-fit/llamacpp/source-build/status",
                     "/api/local/v1/model-fit/llamacpp/source-build/cancel",
                     "/api/local/v1/model-fit/llamacpp/source-build/remove"
                 })
        {
            AssertEx.True(paths.TryGetProperty(path, out _), $"Expected source-build path '{path}'.");
        }

        // The superseded CUDA-only twin of this family was removed; nothing may re-publish it.
        AssertEx.False(paths.TryGetProperty("/api/local/v1/model-fit/llamacpp/cuda-build", out _),
            "The legacy cuda-build route family must stay off the local OpenAPI document.");

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
    public async Task LocalOpenApiDocument_DescribesRuntimeAcquisitionHydrateSurface()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var paths = document.RootElement.GetProperty("paths");

        const string acquisitionPath = "/api/local/v1/model-fit/llamacpp/acquisition";
        AssertEx.True(paths.TryGetProperty(acquisitionPath, out var acquisition),
            $"Expected the runtime-acquisition hydrate path '{acquisitionPath}'.");
        AssertResponses(paths, acquisitionPath, "get", ["200"]);

        // Read-only by contract: the hydrate is queried on every mount, so a mutating verb on this route would kick off a
        // multi-hundred-MB runtime download from a page load.
        foreach (var verb in HttpVerbs.Where(static verb => verb != "get"))
        {
            AssertEx.False(acquisition.TryGetProperty(verb, out _),
                $"The runtime-acquisition hydrate must expose GET only; found {verb.ToUpperInvariant()}.");
        }

        // The hydrate response must mirror the hub push payload field-for-field — the client reconciles both through one
        // shape and one sequence comparison, so a missing field here silently breaks the late-join case.
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        AssertSchemaProperties(schemas, "RuntimeAcquisitionStatusResponse",
        [
            "sequence", "phase", "variant", "tag", "completedBytes", "totalBytes", "stepIndex", "stepCount",
            "sanitizedError"
        ]);
    }

    [Test]
    public async Task LocalOpenApiDocument_DescribesGgufImportAndBenchmarkSurfaces()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var paths = document.RootElement.GetProperty("paths");

        foreach (var path in new[]
                 {
                     "/api/local/v1/model-fit/gguf/import/capability",
                     "/api/local/v1/model-fit/gguf/imports",
                     "/api/local/v1/model-fit/gguf/imports/{operationId}",
                     "/api/local/v1/model-fit/gguf/imports/{operationId}/cancel",
                     "/api/local/v1/benchmarks/projects",
                     "/api/local/v1/benchmarks/projects/{projectId}",
                     "/api/local/v1/benchmarks/projects/{projectId}/runs",
                     "/api/local/v1/benchmarks/projects/{projectId}/items",
                     "/api/local/v1/benchmarks/projects/{projectId}/items/{itemId}",
                     "/api/local/v1/benchmarks/projects/{projectId}/items/order",
                     "/api/local/v1/benchmarks/projects/{projectId}/cells",
                     "/api/local/v1/benchmarks/runs/{runId}",
                     "/api/local/v1/benchmarks/runs/{runId}/cancel",
                     "/api/local/v1/benchmarks/runs/{runId}/score",
                     "/api/local/v1/benchmarks/eligible-agents",
                     "/api/local/v1/benchmarks/eligible-models"
                 })
        {
            AssertEx.True(paths.TryGetProperty(path, out _), $"Expected local model-management path '{path}'.");
        }

        AssertResponses(paths, "/api/local/v1/benchmarks/projects/{projectId}/runs", "post", ["202"]);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        AssertSchemaEnum(schemas, "LocalModelOrigin", ["huggingface", "imported", "trained"]);
        AssertSchemaProperties(schemas, "GgufAcquisitionStatusResponse",
        [
            "operationId", "operationKind", "modelName", "phase", "completedBytes", "totalBytes", "startedAtUtc",
            "updatedAtUtc", "errorCode", "sanitizedMessage"
        ]);
        // `judge` is an OBJECT now, not the flat `judgeStatus` string this used to name: judging moved onto attempts,
        // so a run's judge state is derived rather than stored. The rest are the members later slices added and that
        // the SPA reads off a summary row — a contract change that dropped one of them would take a column with it.
        AssertSchemaProperties(schemas, "BenchmarkRunSummaryResponse",
        [
            "id", "projectId", "primaryModelName", "primaryModelOrigin", "modelContentFingerprint", "primaryStatus",
            "judge", "modelGroupKey", "primaryStopReason", "ttftMs", "repeatGroupId", "isWarmup", "lastStreamSequence",
            "version"
        ]);
        AssertSchemaProperties(schemas, "BenchmarkRunJudgeResponse", ["state"]);
        AssertDeclaredSchemaProperties(schemas, "BenchmarkRunDetailResponse", ["outputParts", "judgeResult"]);
    }

    [Test]
    public async Task LocalOpenApiDocument_DescribesTranscriptionSessionSurface()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var paths = document.RootElement.GetProperty("paths");

        // The four session paths carry six operations. They are declared on every node, feature switch or not, so the
        // generated client does not depend on the node that produced the spec.
        AssertResponses(paths, "/api/local/v1/transcription/sessions", "get", ["200", "400"]);
        AssertResponses(paths, "/api/local/v1/transcription/sessions", "post", ["200", "400"]);
        AssertResponses(paths, "/api/local/v1/transcription/sessions/{sessionId}", "get", ["200", "404"]);
        AssertResponses(paths, "/api/local/v1/transcription/sessions/{sessionId}", "delete", ["204", "404"]);
        AssertResponses(paths, "/api/local/v1/transcription/sessions/{sessionId}/cancel", "post", ["204", "404"]);
        AssertResponses(paths, "/api/local/v1/transcription/sessions/{sessionId}/file", "post", ["200", "400", "404", "415"]);

        // The upload declares a multipart body even though nothing binds one: form auto-binding is off so the audio is
        // never buffered to a framework temp file, which leaves the metadata as the only description of the request.
        var uploadContent = paths.GetProperty("/api/local/v1/transcription/sessions/{sessionId}/file")
                                 .GetProperty("post")
                                 .GetProperty("requestBody")
                                 .GetProperty("content");
        AssertEx.True(uploadContent.TryGetProperty("multipart/form-data", out _),
            "The audio upload must declare a multipart/form-data body.");

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        AssertSchemaProperties(schemas, "TranscriptionSessionDetailResponse", ["session", "segments", "config", "errorCode", "errorMessage"]);
        AssertSchemaProperties(schemas, "TranscriptionSessionSummaryResponse", ["id", "title", "status", "sourceKind", "modelId", "segmentCount"]);
        AssertSchemaProperties(schemas, "TranscriptSegmentResponse", ["seq", "startMs", "endMs", "text", "channel"]);
        AssertSchemaProperties(schemas,
            "TranscriptionUnsupportedContainerResponse",
            ["reason", "message", "detectedContainer", "supportedContainers", "ffmpegRequired"]);

        AssertUploadSchemaRequiresTheFile(schemas);
        AssertCreateSessionSchemaKeepsTheLanguageOverrideOptional(schemas);
    }

    /// <summary>
    ///     The upload's <c>file</c> member is required and non-nullable on the wire.
    /// </summary>
    /// <remarks>
    ///     Nothing binds it — form auto-binding is off so the audio is never buffered to a framework temp file — which
    ///     makes the schema metadata the ONLY description of the request, and it was describing an endpoint that does
    ///     not exist: the handler answers 400 for a form carrying no file, while the document said the file could be
    ///     absent or null. A generated client types its request body off exactly this, so the pin belongs here rather
    ///     than in a comment on a property that is never read.
    /// </remarks>
    private static void AssertUploadSchemaRequiresTheFile(JsonElement schemas)
    {
        var schema = FindSchema(schemas, "UploadTranscriptionAudioRequest");
        var required = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(static member => member.GetString()).ToArray()
            : [];

        AssertEx.Contains(required, "file", $"The uploaded file must be required on the wire; required = [{string.Join(", ", required)}].");

        var file = schema.GetProperty("properties").GetProperty("file");
        AssertEx.False(file.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean(),
            "A nullable file describes a request the endpoint refuses with a 400.");
    }

    /// <summary>
    ///     <c>languageOverride</c> is only required when <c>languageMode</c> is <c>override</c>, and a conditional rule
    ///     must not reach the schema at all.
    /// </summary>
    /// <remarks>
    ///     A real pin, because the leak has already happened once: written as a block
    ///     <c>When(pred, () =&gt; RuleFor(...).NotEmpty().Length(2, 8))</c>, FastEndpoints' validation schema processor
    ///     saw an UNCONDITIONAL rule and published the member as <c>required</c> with <c>minLength: 2</c>. The
    ///     generated client then refused every <c>languageMode: "auto"</c> create request before it left the browser —
    ///     a break no backend test could see, because the server was answering correctly the whole time. Only the
    ///     chained <c>.When(...)</c> form sets the per-component condition the processor reads.
    /// </remarks>
    private static void AssertCreateSessionSchemaKeepsTheLanguageOverrideOptional(JsonElement schemas)
    {
        var schema = FindSchema(schemas, "CreateTranscriptionSessionRequest");
        var required = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(static entry => entry.GetString()).ToArray()
            : [];

        // The control: without it this would pass against a schema that required nothing at all.
        AssertEx.Contains(required, "sourceKind", "sourceKind is unconditionally required and must stay in the schema.");

        AssertEx.False(required.Contains("languageOverride"),
            "languageOverride must not be required: it is conditional on languageMode, and a required member here "
            + "makes the generated client refuse every languageMode 'auto' create request.");

        var languageOverride = schema.GetProperty("properties").GetProperty("languageOverride");
        AssertEx.False(languageOverride.TryGetProperty("minLength", out _),
            "A conditional minimum length must not reach the schema either — it fails the same requests client-side.");
    }

    [Test]
    public async Task LocalOpenApiDocument_DescribesImageRuntimeSourceBuildSurface()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
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

    /// <summary>
    ///     The only drift gate that can see a NEW path: <c>openapi:check</c> regenerates the client from the committed
    ///     spec, so it passes happily while an endpoint is missing from it.
    ///     <para>
    ///         The shared fixture runs with the shipped <c>WorkSessions:Enabled: true</c>, so this test asserts the
    ///         surface an ENABLED node publishes. That the same surface survives the feature being switched off — the
    ///         second half of the design, since registration is unconditional and only behaviour is gated — is
    ///         <see cref="LocalOpenApiDocument_DescribesWorkSessionSurface_WhenTheFeatureIsDisabled" />'s job.
    ///     </para>
    /// </summary>
    [Test]
    public async Task LocalOpenApiDocument_DescribesWorkSessionSurface()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var paths = document.RootElement.GetProperty("paths");

        AssertWorkSessionPaths(paths);

        AssertResponses(paths, "/api/local/v1/work-sessions/{sessionId}/start", "post", ["202", "404", "409"]);
        AssertResponses(paths, "/api/local/v1/work-sessions/{sessionId}/artifacts/{artifactId}/content", "get", ["404", "413"]);

        // The blob path never leaves the process, so it must never appear in the artifact schema either.
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        AssertSchemaProperties(schemas,
            "WorkSessionArtifactResponse",
            ["id", "sequence", "kind", "name", "mediaType", "contentSha256", "sizeBytes", "isValid", "createdStep"]);
    }

    /// <summary>
    ///     The half the shared fixture cannot prove: the work-session routes are mapped unconditionally, so a node with
    ///     <c>WorkSessions:Enabled: false</c> publishes exactly the same OpenAPI surface — the generated hey-api client
    ///     describes every node, not only the ones with the feature on. Behaviour is gated in request-path middleware,
    ///     which the document never sees.
    /// </summary>
    [Test]
    public async Task LocalOpenApiDocument_DescribesWorkSessionSurface_WhenTheFeatureIsDisabled()
    {
        await using var disabledFactory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["WorkSessions:Enabled"] = "false"
            }
        };

        using var client = disabledFactory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);

        AssertWorkSessionPaths(document.RootElement.GetProperty("paths"));

        // Proof the overlay actually took: the same client gets the disabled node's 404 from the request-path
        // middleware. Without this the test would pass identically on a factory whose configuration never applied.
        using var probe = await client.GetAsync("/api/local/v1/work-sessions");
        AssertEx.Equal(HttpStatusCode.NotFound, probe.StatusCode, "A disabled node must refuse the route the document still describes.");
    }

    /// <summary>
    ///     External apps ship with <c>ExternalApps:Enabled</c> false, so the interesting half is the disabled node: the
    ///     routes and the hub are mapped unconditionally and only behaviour is gated, which is what lets one generated
    ///     hey-api client describe every node rather than only the ones that had the feature switched on.
    /// </summary>
    [Test]
    public async Task LocalOpenApiDocument_DescribesExternalAppSurface_WhenTheFeatureIsDisabled()
    {
        await using var disabledFactory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ExternalApps:Enabled"] = "false"
            }
        };

        using var client = disabledFactory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);

        AssertExternalAppPaths(document.RootElement.GetProperty("paths"));

        // Proof the overlay actually took: the same client gets the disabled node's 404 from the request-path
        // middleware. Without this the test would pass identically on a factory whose configuration never applied.
        using var probe = await client.GetAsync("/api/local/v1/external-apps/catalog");
        AssertEx.Equal(HttpStatusCode.NotFound, probe.StatusCode, "A disabled node must refuse the route the document still describes.");
    }

    /// <summary>
    ///     The uninstall reads <c>expectedVersion</c> from the QUERY, and the document has to say so. It described a
    ///     request body instead, so the generated client sent the version where the endpoint never looks and every
    ///     delete it made would have 400d on the presence rule. Endpoint tests cannot catch this: they build the
    ///     request themselves. The control is the POST beside it, whose version genuinely is a body member.
    /// </summary>
    [Test]
    public async Task LocalOpenApiDocument_DeclaresTheUninstallExpectedVersionAsAQueryParameter()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var paths = document.RootElement.GetProperty("paths");

        var uninstall = paths.GetProperty("/api/local/v1/external-apps/instances/{instanceId}").GetProperty("delete");
        var parameters = uninstall.GetProperty("parameters")
                                  .EnumerateArray()
                                  .Select(static parameter => (Name: parameter.GetProperty("name").GetString(),
                                      In: parameter.GetProperty("in").GetString()))
                                  .ToArray();

        AssertEx.Contains(parameters,
            static parameter => parameter is { Name: "expectedVersion", In: "query" },
            $"The uninstall's version is a query parameter; declared = [{string.Join(", ", parameters.Select(static parameter => $"{parameter.Name}:{parameter.In}"))}].");
        AssertEx.False(uninstall.TryGetProperty("requestBody", out _),
            "A DELETE that declares a body makes the generated client send the version where the endpoint never reads it.");

        // The control: start takes the same version in a POST body, so a spec that moved everything to the query
        // would fail here rather than pass both halves.
        var start = paths.GetProperty("/api/local/v1/external-apps/instances/{instanceId}/start").GetProperty("post");
        AssertEx.True(start.TryGetProperty("requestBody", out _), "start sends expectedVersion in its body.");
    }

    /// <summary>
    ///     Every declared request body describes something the endpoint really reads, and no bodyless verb declares one
    ///     it does not.
    /// </summary>
    /// <remarks>
    ///     The generator strips route- and query-bound members from the ONE schema it emits per request type, in place,
    ///     and then decides per operation whether anything is left to send. A request type shared by two endpoints that
    ///     bind its members differently therefore answers that question differently depending on which endpoint the
    ///     generator reached first — so moving endpoints between files changed the contract. The two shapes that leak
    ///     are a body on a verb that carries none and a body whose schema can hold nothing; both are refused here.
    /// </remarks>
    [Test]
    public async Task LocalOpenApiDocument_DeclaresARequestBodyOnlyWhereOneIsRead()
    {
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var operations = 0;
        var seenDeletesWithABody = new List<string>();
        var bodyOnABodylessVerb = new List<string>();
        var bodyThatCanCarryNothing = new List<string>();

        foreach (var pathItem in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!pathItem.Name.StartsWith("/api/local/v1/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var operation in pathItem.Value.EnumerateObject())
            {
                if (!HttpVerbs.Contains(operation.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                operations++;
                if (!operation.Value.TryGetProperty("requestBody", out var requestBody))
                {
                    continue;
                }

                var operationId = operation.Value.TryGetProperty("operationId", out var id) ? id.GetString() ?? string.Empty : string.Empty;
                var label = $"{operation.Name.ToUpperInvariant()} {pathItem.Name} ({operationId})";

                if (operation.Name is "get" or "head")
                {
                    bodyOnABodylessVerb.Add(label);
                }
                else if (operation.Name == "delete")
                {
                    if (DeleteOperationsThatReadABody.Contains(operationId, StringComparer.Ordinal))
                    {
                        seenDeletesWithABody.Add(operationId);
                    }
                    else
                    {
                        bodyOnABodylessVerb.Add(label);
                    }
                }

                if (IsEmptySchema(schemas, requestBody))
                {
                    bodyThatCanCarryNothing.Add(label);
                }
            }
        }

        // Non-vacuity floor under the operation count this document publishes: a document that generated no paths would
        // otherwise pass every assertion below with nothing to check.
        AssertEx.True(operations >= 400,
            $"Only {operations} operations were found under /api/local/v1/; refusing a vacuous pass.");

        AssertEx.Empty(bodyOnABodylessVerb,
            "A GET or DELETE that declares a request body makes the generated client demand one the endpoint never "
            + "reads, and it is the shape an endpoint registration reorder produces when a request type is shared with "
            + "an endpoint that binds its members elsewhere. Add a genuinely body-reading DELETE to "
            + $"{nameof(DeleteOperationsThatReadABody)} deliberately:"
            + Environment.NewLine + string.Join(Environment.NewLine, bodyOnABodylessVerb.Order(StringComparer.Ordinal)));

        AssertEx.Empty(bodyThatCanCarryNothing,
            "A request body whose schema declares no members describes a payload with nothing in it — the generated "
            + "client is made to send an empty object. This is what the allowlist above must never be used to hide:"
            + Environment.NewLine + string.Join(Environment.NewLine, bodyThatCanCarryNothing.Order(StringComparer.Ordinal)));

        var stale = DeleteOperationsThatReadABody.Except(seenDeletesWithABody, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        AssertEx.Empty(stale,
            "The allowlist is shrink-only: these operations no longer declare a DELETE body, so they must be removed "
            + "from it rather than left to excuse a future one: " + string.Join(", ", stale));
    }

    /// <summary>
    ///     Whether the body's JSON schema resolves to a component that declares no members at all.
    /// </summary>
    private static bool IsEmptySchema(JsonElement schemas, JsonElement requestBody)
    {
        if (!requestBody.TryGetProperty("content", out var content)
            || !content.TryGetProperty("application/json", out var json)
            || !json.TryGetProperty("schema", out var schema)
            || !schema.TryGetProperty("$ref", out var reference)
            || reference.GetString() is not { Length: > 0 } pointer)
        {
            return false;
        }

        var name = pointer[(pointer.LastIndexOf('/') + 1)..];

        return schemas.TryGetProperty(name, out var resolved)
               && !resolved.TryGetProperty("properties", out _)
               && !resolved.TryGetProperty("allOf", out _);
    }

    /// <summary>
    ///     The update contracts' requiredness, which is not a formality: the generated client types a request body off
    ///     these arrays, so a member in the wrong bucket ships a caller that either omits a mandatory field or is made
    ///     to invent one. Both were wrong here, and for opposite reasons — <c>version</c> is required by the endpoint
    ///     but was emitted optional (the schema processor reads requiredness off a rule's condition and off
    ///     <c>required</c> members, never off <c>GreaterThan(0)</c>), while the partial-update <c>name</c> is
    ///     omit-means-unchanged and was emitted required (a block <c>When(...)</c> sets no per-component condition, so
    ///     the processor saw a bare <c>NotEmpty</c>).
    /// </summary>
    [Test]
    public async Task LocalOpenApiDocument_MarksUpdateRequestVersionRequiredAndPartialMembersOptional()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        AssertRequired(schemas, "UpdateGraphWorkflowDefinitionRequest", required: ["version"], optional: ["name"]);
        AssertRequired(schemas, "UpdateDevWorkflowDefinitionRequest", required: ["version"], optional: ["name"]);

        // The rule set is the control: it is a WHOLE-document PUT, so its name and body are genuinely required and
        // must stay that way — without this the test above would also pass on a spec that made everything optional.
        AssertRequired(schemas, "UpdateDevWorkflowRuleSetRequest", required: ["version", "name", "body"], optional: []);

        // The create routes answer 201, and the client narrows the created body off that response.
        var paths = document.RootElement.GetProperty("paths");
        AssertResponses(paths, "/api/local/v1/development-workflows/definitions", "post", ["201", "400"]);
        AssertResponses(paths, "/api/local/v1/development-workflows/rule-sets", "post", ["201", "400"]);
        AssertResponses(paths, "/api/local/v1/development-workflows/work-items", "post", ["201", "400"]);
    }

    /// <summary>
    ///     Every route whose service reaches <c>TrainingConflictException</c> answers 409 through the globally
    ///     registered <c>TrainingExceptionHandler</c>, which no endpoint code shows. A route can therefore lose its
    ///     <c>.Produces&lt;TrainingErrorResponse&gt;(409)</c> and keep emitting 409, and <c>openapi:check</c> stays
    ///     green because it regenerates the client from the committed spec. This is the only gate on that drift.
    /// </summary>
    [Test]
    public async Task LocalOpenApiDocument_DeclaresTheTrainingConflictStatusOnEveryRouteThatEmitsIt()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var paths = document.RootElement.GetProperty("paths");

        // Create-run reaches VersionConflict, DatasetNotReady and BaseArtifactNotReady inside the store's create
        // transaction; VersionConflict is the stale-confirmation-dialog case ExpectedDatasetVersion exists to catch.
        AssertResponses(paths, "/api/local/v1/training/runs", "post", ["200", "400", "409"]);
        AssertResponses(paths, "/api/local/v1/training/comparisons", "post", ["200", "400", "409"]);
        AssertResponses(paths, "/api/local/v1/training/comparisons/{comparisonId}", "delete", ["204", "409"]);
        AssertResponses(paths, "/api/local/v1/training/evaluations/{evaluationId}", "delete", ["204", "409"]);

        AssertSchemaProperties(document.RootElement.GetProperty("components").GetProperty("schemas"),
            "TrainingErrorResponse",
            ["code", "message"]);
    }

    /// <summary>
    ///     The node-settings save declares ONLY its 409 explicitly: FastEndpoints advertises the 200, and the host's
    ///     <c>Errors.UseProblemDetails()</c> advertises the ProblemDetails 400 that both the boundary validator and the
    ///     global <c>DomainValidationExceptionHandler</c> (the unreadable-settings arm included) send. Declaring either
    ///     explicitly re-labels the 400 as the FastEndpoints <c>ErrorResponse</c> shape, which is NOT what this endpoint
    ///     sends — and a second 409 schema is impossible on one operation. "We deliberately declared nothing new" is
    ///     invisible in the endpoint, so it is pinned here: this is what fails when a later change adds a Produces for a
    ///     newly-mapped exception.
    ///     <para>
    ///         Three statuses are the endpoint's OWN contract. The wire set is five: the operator authorization policy
    ///         makes FastEndpoints add 401 and 403, which no endpoint code declares either. The assertion pins the whole
    ///         set, because that is the only form a new Produces cannot slip past.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SaveNodeSettings_DeclaresOnlyItsThreeStatuses()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var save = document.RootElement.GetProperty("paths").GetProperty("/api/local/v1/node-settings").GetProperty("put");

        var declared = save.GetProperty("responses").EnumerateObject().Select(static status => status.Name).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Equal("200, 400, 401, 403, 409", string.Join(", ", declared),
            $"The node-settings save must declare its own 200/400/409 plus the policy's 401/403 and no more — a Produces for a globally-handled exception re-labels the already-advertised ProblemDetails 400. Declared: [{string.Join(", ", declared)}].");

        var conflictSchema = save.GetProperty("responses").GetProperty("409")
                                 .GetProperty("content").GetProperty("application/json").GetProperty("schema")
                                 .GetProperty("$ref").GetString();
        AssertEx.Contains(conflictSchema, "NodeSettingsConflictResponse", StringComparison.Ordinal);
    }

    [Test]
    public async Task Refresh_DeclaresTheUnauthorizedItActuallySends()
    {
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var refresh = document.RootElement.GetProperty("paths").GetProperty("/api/local/v1/auth/refresh").GetProperty("post");

        var declared = refresh.GetProperty("responses").EnumerateObject().Select(static status => status.Name).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Equal("200, 401, 403", string.Join(", ", declared),
            $"auth/refresh must declare its 200, the 401 a failed refresh sends and the middleware's 403. Declared: [{string.Join(", ", declared)}].");
    }

    private static void AssertRequired(JsonElement schemas, string schemaSuffix, IReadOnlyList<string> required, IReadOnlyList<string> optional)
    {
        var schema = FindSchema(schemas, schemaSuffix);
        var declared = schema.TryGetProperty("required", out var requiredArray)
            ? requiredArray.EnumerateArray().Select(static member => member.GetString() ?? string.Empty).ToArray()
            : [];

        foreach (var member in required)
        {
            AssertEx.Contains(declared, member, $"{schemaSuffix}.{member} must be required on the wire; required = [{string.Join(", ", declared)}].");
        }

        foreach (var member in optional)
        {
            AssertEx.False(declared.Contains(member, StringComparer.Ordinal),
                $"{schemaSuffix}.{member} means \"leave it alone\" when omitted, so it must not be required; required = [{string.Join(", ", declared)}].");
        }
    }

    private static void AssertExternalAppPaths(JsonElement paths)
    {
        foreach (var (path, verbs) in ExternalAppPaths)
        {
            AssertEx.True(paths.TryGetProperty(path, out var pathItem), $"Expected external-apps path '{path}'.");
            foreach (var verb in verbs)
            {
                AssertEx.True(pathItem.TryGetProperty(verb, out _), $"Expected {verb.ToUpperInvariant()} {path}.");
            }
        }

        AssertEx.Equal(expected: 18, ExternalAppPaths.Length, "the family is 18 distinct paths.");
        AssertEx.Equal(expected: 20, ExternalAppPaths.Sum(static entry => entry.Verbs.Length), "carrying 20 operations.");
    }

    private static void AssertWorkSessionPaths(JsonElement paths)
    {
        foreach (var (path, verbs) in WorkSessionPaths)
        {
            AssertEx.True(paths.TryGetProperty(path, out var pathItem), $"Expected work-session path '{path}'.");
            foreach (var verb in verbs)
            {
                AssertEx.True(pathItem.TryGetProperty(verb, out _), $"Expected {verb.ToUpperInvariant()} {path}.");
            }
        }
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

    private static void AssertDeclaredSchemaProperties(JsonElement schemas, string schemaSuffix, IReadOnlyList<string> expected)
    {
        var schema = FindSchema(schemas, schemaSuffix);
        var propertySets = new List<JsonElement>();
        if (schema.TryGetProperty("properties", out var directProperties))
        {
            propertySets.Add(directProperties);
        }

        if (schema.TryGetProperty("allOf", out var allOf))
        {
            propertySets.AddRange(allOf.EnumerateArray()
                                       .Where(static item => item.TryGetProperty("properties", out _))
                                       .Select(static item => item.GetProperty("properties")));
        }

        foreach (var property in expected)
        {
            AssertEx.True(propertySets.Any(properties => properties.TryGetProperty(property, out _)),
                $"Expected {schemaSuffix}.{property} in OpenAPI.");
        }
    }

    private static void AssertSchemaEnum(JsonElement schemas, string schemaSuffix, IReadOnlyList<string> expected)
    {
        var schema = FindSchema(schemas, schemaSuffix);
        var values = schema.GetProperty("enum").EnumerateArray().Select(static value => value.GetString() ?? string.Empty).ToArray();
        AssertEx.Equal(string.Join('|', expected), string.Join('|', values));
    }

    /// <summary>
    ///     Every node-local operation declares a <c>403</c>, because every one of them can actually return it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>LocalApiSecurityMiddleware</c> sits in front of the whole <c>/api/local/v1/</c> prefix and answers a
    ///         real 403 — not a 401 — for a request carrying a foreign <c>Origin</c> or <c>Host</c>, or arriving from a
    ///         non-loopback peer. That check runs before authorization and does not care whether the route is
    ///         anonymous, so the four anonymous auth operations (login, setup, status, refresh) carry the status as
    ///         truthfully as the authorized ones.
    ///     </para>
    ///     <para>
    ///         Pinned because the declaration arrives by a route nobody chose: FastEndpoints adds the auto-403 off
    ///         <c>PreBuiltUserPolicies</c>, which the deny-by-default <c>Configurator</c> in <c>Program</c> sets on
    ///         every endpoint — while the auto-401 beside it IS suppressed for an anonymous verb. A future library
    ///         version that made the two symmetric would silently drop the 403 from the generated client's error
    ///         handling for every route, and nothing else in the suite would notice. The generated hey-api client is
    ///         built from this document, so what it declares is what the SPA is written against.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task LocalOpenApiDocument_DeclaresForbiddenOnEveryOperation()
    {
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);

        var operations = 0;
        var offenders = new List<string>();

        foreach (var pathItem in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!pathItem.Name.StartsWith("/api/local/v1/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var operation in pathItem.Value.EnumerateObject())
            {
                if (!HttpVerbs.Contains(operation.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                operations++;

                if (!operation.Value.TryGetProperty("responses", out var responses)
                    || !responses.TryGetProperty("403", out _))
                {
                    offenders.Add($"{operation.Name.ToUpperInvariant()} {pathItem.Name}");
                }
            }
        }

        // Non-vacuity floor under the 445 operations measured on 2026-09-18: a document that generated no paths, or a
        // prefix filter that matched none of them, would otherwise pass this with zero offenders.
        AssertEx.True(operations >= 400,
            $"Only {operations} operations were found under /api/local/v1/; refusing a vacuous pass. The document or the path filter is broken.");

        AssertEx.Empty(offenders,
            "Every node-local operation must declare a 403: LocalApiSecurityMiddleware guards the whole "
            + "/api/local/v1/ prefix and answers 403 for a foreign Origin/Host or a non-loopback peer, before "
            + "authorization and regardless of whether the route is anonymous. The operation(s) below no longer say "
            + "so, which means the generated client is not written to handle a response the node really sends:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));
    }

    private async Task<List<string>> GetOperationIdsAsync()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);

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
