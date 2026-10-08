namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompatible;

using System.Net.Http.Headers;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one handler every external-connection request passes through: it sets each configured header, replaces
///     rather than duplicates, and never lets a reserved name override authentication or transport headers.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CustomRequestHeadersHandlerTests
{
    [Test]
    public async Task SendAsync_SetsEveryConfiguredHeader()
    {
        var recorder = new OpenAiWireRecorder();
        using var client = CreateClient(recorder, [new("X-Alpha", "one"), new("X-Beta", "two")]);

        using var response = await client.GetAsync(new Uri("http://127.0.0.1:1/v1/models"));

        AssertEx.Equal("one", recorder.LastRequest.Headers.GetValueOrDefault("X-Alpha"));
        AssertEx.Equal("two", recorder.LastRequest.Headers.GetValueOrDefault("X-Beta"));
    }

    [Test]
    public async Task SendAsync_ReplacesAHeaderTheRequestAlreadyCarries()
    {
        var recorder = new OpenAiWireRecorder();
        using var client = CreateClient(recorder, [new("X-Alpha", "configured")]);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1:1/v1/models"));
        request.Headers.Add("X-Alpha", "caller");

        using var response = await client.SendAsync(request);

        AssertEx.Equal("configured", recorder.LastRequest.Headers.GetValueOrDefault("X-Alpha"));
    }

    [Test]
    [Arguments("Authorization")]
    [Arguments("authorization")]
    [Arguments("API-KEY")]
    [Arguments("Host")]
    [Arguments("Cookie")]
    public async Task SendAsync_SkipsAReservedName_SoAuthenticationIsNeverOverridden(string reserved)
    {
        var recorder = new OpenAiWireRecorder();
        using var client = CreateClient(recorder, [new(reserved, "attacker"), new("X-Ok", "ok")]);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1:1/v1/models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "real");

        using var response = await client.SendAsync(request);

        AssertEx.Equal("Bearer real", recorder.LastRequest.Authorization);
        AssertEx.False(recorder.LastRequest.Headers.Values.Any(static value => value.Contains("attacker", StringComparison.Ordinal)));
        AssertEx.Equal("ok", recorder.LastRequest.Headers.GetValueOrDefault("X-Ok"));
    }

    [Test]
    [Arguments("Content-Language")]
    [Arguments("expires")]
    [Arguments("Last-Modified")]
    [Arguments("Bad Name")]
    [Arguments("X-Bad\r\nName")]
    [Arguments("")]
    public async Task SendAsync_ANameTheRequestCannotCarry_IsSkippedRatherThanFailingTheRequest(string name)
    {
        // .NET throws "Misused header name" for a content header on a request and rejects a malformed name; either would
        // fail every chat, health and probe request on the connection if it escaped the handler.
        var recorder = new OpenAiWireRecorder();
        using var client = CreateClient(recorder, [new(name, "x"), new("X-Ok", "ok")]);

        using var response = await client.GetAsync(new Uri("http://127.0.0.1:1/v1/models"));

        AssertEx.Equal(1, recorder.Requests.Count);
        AssertEx.Equal("ok", recorder.LastRequest.Headers.GetValueOrDefault("X-Ok"));
    }

    // The handler chain transfers into the HttpClient (disposeHandler: true), which the caller disposes; CA2000 cannot
    // follow that ownership transfer.
#pragma warning disable CA2000
    private static HttpClient CreateClient(OpenAiWireRecorder recorder, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        return new HttpClient(new CustomRequestHeadersHandler(headers, recorder.CreateHandler()), disposeHandler: true);
    }
#pragma warning restore CA2000
}
