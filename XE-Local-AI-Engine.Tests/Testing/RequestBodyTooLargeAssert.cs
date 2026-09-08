namespace XE_Local_AI_Engine.Tests.Testing;

using System.Text.Json;

/// <summary>
///     The ONE 413 body a capped route is allowed to answer with. A capped route has two refusal paths — Kestrel's,
///     mid-read, and the endpoint's own Content-Length exit — and both DECLARE ASP.NET's <c>ProblemDetails</c> via
///     <c>ProducesProblem(413)</c>. The endpoint half used to send FastEndpoints' <c>errors[]</c> body instead, which
///     the generated client cannot parse; asserting the status alone could not see that, so these routes assert the
///     shape.
/// </summary>
internal static class RequestBodyTooLargeAssert
{
    /// <param name="response">The refusal to inspect.</param>
    /// <param name="because">Names the route under test, so a failure says which of the capped routes drifted.</param>
    public static async Task DeclaredProblemShapeAsync(HttpResponseMessage response, string because)
    {
        ArgumentNullException.ThrowIfNull(response);

        AssertEx.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType,
            $"{because} must answer the problem+json media type it declares, not application/json.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var body = document.RootElement;

        AssertEx.Equal(expected: 413, body.GetProperty("status").GetInt32(), because);
        AssertEx.Equal("https://tools.ietf.org/html/rfc7231#section-6.5.11", body.GetProperty("type").GetString(), because);
        AssertEx.Equal("Request body too large", body.GetProperty("title").GetString(), $"{because} must carry the same title the host's own refusal writes.");
        AssertEx.NotEmpty(body.GetProperty("detail").GetString(), $"{because} must name the cap it enforces.");
        AssertEx.True(body.TryGetProperty("traceId", out var traceId), $"{because}: every problem body on this surface carries a traceId.");
        AssertEx.NotEmpty(traceId.GetString(), $"{because}: an empty traceId correlates nothing.");
        AssertEx.False(body.TryGetProperty("errors", out _),
            $"{because} must not answer FastEndpoints' errors[] shape — that is the second shape for one status this replaced.");
    }
}
