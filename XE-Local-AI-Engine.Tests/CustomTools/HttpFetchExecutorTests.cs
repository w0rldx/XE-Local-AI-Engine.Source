namespace XE_Local_AI_Engine.Tests.CustomTools;

using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     HTTP fetch request assembly. A custom fetch tool cannot be exercised end-to-end against a local test server (the
///     SSRF guard denies loopback by design), so these assert <see cref="HttpFetchExecutor.BuildRequest" /> directly:
///     {param} placeholders in a header value are substituted, and a fixed header is passed through unchanged.
/// </summary>
public sealed class HttpFetchExecutorTests
{
    [Test]
    public async Task BuildRequest_SubstitutesPlaceholderInHeaderValue()
    {
        const string configJson = """
                                  {"method":"GET","urlTemplate":"https://api.example.com/data","headers":[{"name":"Authorization","value":"Bearer {token}","isSecret":false}],"bodyTemplate":null,"allowedHosts":[]}
                                  """;
        const string parametersJson = """[{"name":"token","type":"string","description":"","required":true}]""";
        var config = CustomToolConfigParser.ParseHttpFetch(configJson);
        var parameters = CustomToolConfigParser.ParseParameters(parametersJson);

        using var request = HttpFetchExecutor.BuildRequest(config, parameters, """{"token":"abc123"}""");

        AssertEx.True(request.Headers.TryGetValues("Authorization", out var values), "Authorization header must be present.");
        AssertEx.Equal("Bearer abc123", string.Join(string.Empty, values!));
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequest_FixedHeader_PassesThroughUnchanged()
    {
        const string configJson = """
                                  {"method":"GET","urlTemplate":"https://api.example.com/data","headers":[{"name":"X-Api-Key","value":"static-key-value","isSecret":true}],"bodyTemplate":null,"allowedHosts":[]}
                                  """;
        var config = CustomToolConfigParser.ParseHttpFetch(configJson);

        using var request = HttpFetchExecutor.BuildRequest(config, [], "{}");

        AssertEx.True(request.Headers.TryGetValues("X-Api-Key", out var values), "X-Api-Key header must be present.");
        AssertEx.Equal("static-key-value", string.Join(string.Empty, values!));
        await Task.CompletedTask;
    }
}
