namespace XE_Local_AI_Engine.Tests.Voice;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Voice.V1;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class VoiceManifestEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetVoiceManifest_WhenVoiceDisabled_ReturnsEnabledFalse()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             VoiceFeatureEnabled = false
                         });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/voice/manifest");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var manifest = await ReadJsonAsync<VoiceManifestResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(manifest.Enabled);
        // The catalog (models/voices) is still surfaced when disabled; the client gates UI on Enabled.
        AssertEx.Equal("af_heart", manifest.DefaultVoiceId);
        AssertEx.Null(manifest.RemoteFallback);
    }

    [Test]
    public async Task GetVoiceManifest_WhenEnabled_ReturnsModelsAndVoices()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(new StoredNodeSettings
                         {
                             VoiceFeatureEnabled = true
                         });
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/voice/manifest");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var manifest = await ReadJsonAsync<VoiceManifestResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(manifest.Enabled);

        var model = AssertEx.NotNull(manifest.Models.SingleOrDefault(entry => entry.Id == "onnx-community/Kokoro-82M-v1.0-ONNX"));
        AssertEx.Equal("en", model.Language);
        // Two recommended dtypes: fp32 (WebGPU) and q8 (WASM) with real integrity hashes + sizes.
        var fp32 = AssertEx.NotNull(model.Files.SingleOrDefault(file => file.Dtype == "fp32"));
        AssertEx.Equal("model.onnx", fp32.File);
        AssertEx.Equal(expected: 325532232L, fp32.ByteSize);
        AssertEx.Equal("8fbea51ea711f2af382e88c833d9e288c6dc82ce5e98421ea61c058ce21a34cb", fp32.Sha256);
        AssertEx.True(fp32.DownloadUrl.EndsWith("/onnx/model.onnx", StringComparison.Ordinal));
        var q8 = AssertEx.NotNull(model.Files.SingleOrDefault(file => file.Dtype == "q8"));
        AssertEx.Equal("model_quantized.onnx", q8.File);
        AssertEx.Equal(expected: 92361116L, q8.ByteSize);
        AssertEx.Equal("fbae9257e1e05ffc727e951ef9b9c98418e6d79f1c9b6b13bd59f5c9028a1478", q8.Sha256);

        AssertEx.ContainsSingle(manifest.Voices, voice => voice.Id == "af_heart" && voice.Gender == "female" && voice.Language == "en");
        AssertEx.ContainsSingle(manifest.Voices, voice => voice.Id == "am_adam" && voice.Gender == "male");
        AssertEx.ContainsSingle(manifest.Voices, voice => voice.Id == "bm_george" && voice.Gender == "male");
        // Kokoro ships no German voice — the manifest must not invent one.
        AssertEx.True(manifest.Voices.All(voice => voice.Language == "en"));
        AssertEx.Equal("af_heart", manifest.DefaultVoiceId);
        AssertEx.Null(manifest.RemoteFallback);
    }

    [Test]
    public async Task GetVoiceManifest_RequiresOperatorPolicy()
    {
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        await using var factory = CreateFactory(nodeSettingsStore);
        using var client = factory.CreateClient();

        // No bearer token attached: the Operator policy must reject the request before composing the manifest.
        using var response = await client.GetAsync("/api/local/v1/voice/manifest").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await nodeSettingsStore.DidNotReceiveWithAnyArgs().LoadAsync(Arg.Any<CancellationToken>());
    }

    private static TestingWebAppFactory CreateFactory(INodeSettingsStore nodeSettingsStore)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton(nodeSettingsStore);
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}
