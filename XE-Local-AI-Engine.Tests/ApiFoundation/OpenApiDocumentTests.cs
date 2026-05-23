namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class OpenApiDocumentTests
{
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
}
