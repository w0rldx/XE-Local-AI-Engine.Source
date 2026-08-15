namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class FastEndpointsProblemDetailsTests
{
    [Test]
    public async Task ValidationFailure_ReturnsProblemDetails()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/diagnostics/validation-probe")
        {
            Content = JsonContent.Create(new
            {
                Name = string.Empty
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.MediaType,
            "problem+json",
            StringComparison.OrdinalIgnoreCase);

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);
        var root = document.RootElement;

        AssertEx.Equal(expected: 400, root.GetProperty("status").GetInt32());
        AssertEx.NotEmpty(root.GetProperty("title").GetString());
        AssertEx.True(root.TryGetProperty("errors", out var errors), "Expected problem details to contain validation errors.");
        AssertEx.Contains(errors.GetRawText(), "Name", StringComparison.OrdinalIgnoreCase);
    }
}
