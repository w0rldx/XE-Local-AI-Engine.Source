namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     AUD4-02: <see cref="LlamaServerHealthProbe.TryReadEffectiveContextTokensAsync" /> parses the effective per-slot
///     context window from the server's <c>/props</c> endpoint (<c>default_generation_settings.n_ctx</c>), degrading to
///     <see langword="null" /> on any unavailability rather than throwing.
/// </summary>
public sealed class LlamaServerHealthProbePropsTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:18100/v1");

    [Test]
    public async Task TryReadEffectiveContextTokens_ParsesNCtxFromProps()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """{"default_generation_settings":{"n_ctx":16384},"model_path":"x"}""");
        using var client = new HttpClient(handler);
        var probe = new LlamaServerHealthProbe(client);

        var effective = await probe.TryReadEffectiveContextTokensAsync(BaseAddress, CancellationToken.None);

        AssertEx.True(effective == 16384, "n_ctx from /props must be parsed as the effective context.");
    }

    [Test]
    public async Task TryReadEffectiveContextTokens_WhenPropsMissingField_ReturnsNull()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """{"default_generation_settings":{"temperature":0.8}}""");
        using var client = new HttpClient(handler);
        var probe = new LlamaServerHealthProbe(client);

        var effective = await probe.TryReadEffectiveContextTokensAsync(BaseAddress, CancellationToken.None);

        AssertEx.True(effective is null, "a /props body without n_ctx yields an unknown effective context.");
    }

    [Test]
    public async Task TryReadEffectiveContextTokens_WhenPropsNotOk_ReturnsNull()
    {
        using var handler = new StubHandler(HttpStatusCode.NotFound, "not found");
        using var client = new HttpClient(handler);
        var probe = new LlamaServerHealthProbe(client);

        var effective = await probe.TryReadEffectiveContextTokensAsync(BaseAddress, CancellationToken.None);

        AssertEx.True(effective is null, "a non-200 /props response yields an unknown effective context.");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
