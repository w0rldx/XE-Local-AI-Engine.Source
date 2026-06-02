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
