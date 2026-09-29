namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Transcription;

/// <summary>
///     <c>GET model-fit/runtime-residents</c> over HTTP: wire shape and strings, 200 without whisper rows when
///     transcription is off, and the operator policy refusing callers before the supervisors are read.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GetRuntimeResidentsEndpointTests
{
    private const string ResidentsRoute = "/api/local/v1/model-fit/runtime-residents";

    [Test]
    public async Task GetResidents_ProjectsImageAndWhisperRowsOntoTheExactWireShape()
    {
        var imageGate = new ImageRuntimeActivityGate();
        using var job = AssertEx.NotNull(imageGate.TryAcquireJobLease());
        var host = new Doubles(imageGate);
        host.ImageSupervisor.GetResidents().Returns([ImageProcess("sd15", leased: true, exited: false), ImageProcess("sdxl", leased: false, exited: true)]);
        host.WhisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Ready, "large-v3-turbo", WhisperBackend.Cuda);

        var (status, body) = await GetAsync(host, factory => factory.AddNodeBearerToken);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("items", PropertyNames(document.RootElement));
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        AssertEx.Equal(expected: 3, items.Length);
        AssertEx.Equal("backend,canEject,modelId,runtime,state", PropertyNames(items[0]));
        AssertEx.Equal("image|sd15|active|null|False", Row(items[0]));
        AssertEx.Equal("image|sdxl|exited|null|False", Row(items[1]));
        AssertEx.Equal("transcription|large-v3-turbo|idle|cuda|True", Row(items[2]));
    }

    [Test]
    public async Task GetResidents_WhileWhisperStartsAndAnImageSpawns_ReportsStartingRowsWithoutAModel()
    {
        var imageGate = new ImageRuntimeActivityGate();
        using var spawn = AssertEx.NotNull(imageGate.TryAcquireSpawnReadinessLease());
        var host = new Doubles(imageGate);
        host.ImageSupervisor.GetResidents().Returns([]);
        host.WhisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Starting, modelId: null, backend: null);

        var (status, body) = await GetAsync(host, factory => factory.AddNodeBearerToken);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        var rows = document.RootElement.GetProperty("items").EnumerateArray().Select(Row).ToArray();
        AssertEx.Equal("image|null|starting|null|False,transcription|null|starting|null|True", string.Join(',', rows));
    }

    [Test]
    public async Task GetResidents_WithTranscriptionDisabled_AnswersOkWithoutWhisperRows()
    {
        var host = new Doubles(new ImageRuntimeActivityGate());
        host.ImageSupervisor.GetResidents().Returns([ImageProcess("sd15", leased: false, exited: false)]);
        host.WhisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Ready, "base", WhisperBackend.Cpu);

        var (status, body) = await GetAsync(host, factory => factory.AddNodeBearerToken, transcriptionEnabled: false);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        var rows = document.RootElement.GetProperty("items").EnumerateArray().Select(Row).ToArray();
        AssertEx.Equal("image|sd15|idle|null|True", string.Join(',', rows));
    }

    [Test]
    public async Task GetResidents_WithANonAdminToken_IsForbiddenBeforeTheSupervisors()
    {
        var host = new Doubles(new ImageRuntimeActivityGate());

        var (status, _) = await GetAsync(host,
            factory => request => request.Headers.Authorization =
                new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, factory.CreateNonOperatorAccessToken("Viewer")));

        AssertEx.Equal(HttpStatusCode.Forbidden, status);
        host.ImageSupervisor.DidNotReceive().GetResidents();
    }

    [Test]
    public async Task GetResidents_WithoutABearerToken_IsUnauthorized()
    {
        var host = new Doubles(new ImageRuntimeActivityGate());

        var (status, _) = await GetAsync(host, static _ => static _ => { });

        AssertEx.Equal(HttpStatusCode.Unauthorized, status);
        host.ImageSupervisor.DidNotReceive().GetResidents();
    }

    private static string Row(JsonElement item)
    {
        static string Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? "null" : value.ToString();

        return string.Join('|',
            Text(item.GetProperty("runtime")),
            Text(item.GetProperty("modelId")),
            Text(item.GetProperty("state")),
            Text(item.GetProperty("backend")),
            Text(item.GetProperty("canEject")));
    }

    private static string PropertyNames(JsonElement element)
    {
        return string.Join(',', element.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
    }

    private static ImageServerResidentSnapshot ImageProcess(string modelName, bool leased, bool exited)
    {
        return new ImageServerResidentSnapshot
        {
            ModelName = modelName,
            HasActiveJobLease = leased,
            HasExited = exited,
            LastUsedUtc = DateTimeOffset.UnixEpoch
        };
    }

    private static WhisperRuntimeStatusSnapshot WhisperStatus(WhisperRuntimeState state, string? modelId, WhisperBackend? backend)
    {
        return new WhisperRuntimeStatusSnapshot
        {
            State = state,
            LoadedModelId = modelId,
            Backend = backend,
            BinaryVersion = null,
            BinarySource = null,
            SupportsTranscode = false
        };
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(Doubles host,
        Func<TestServerWebAppFactory, Action<HttpRequestMessage>> authorize,
        bool transcriptionEnabled = true)
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["Transcription:Enabled"] = transcriptionEnabled ? "true" : "false"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageServerSupervisor>();
                services.AddSingleton(host.ImageSupervisor);
                services.RemoveAll<IImageRuntimeActivityGate>();
                services.AddSingleton<IImageRuntimeActivityGate>(host.ImageGate);
                services.RemoveAll<IWhisperServerSupervisor>();
                services.AddSingleton<IWhisperServerSupervisor>(host.WhisperSupervisor);
                services.RemoveAll<IWhisperRuntimeActivityGate>();
                services.AddSingleton<IWhisperRuntimeActivityGate>(host.WhisperGate);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ResidentsRoute);
        authorize(factory)(request);

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed class Doubles
    {
        public Doubles(ImageRuntimeActivityGate imageGate)
        {
            ImageGate = imageGate;
        }

        public ImageRuntimeActivityGate ImageGate { get; }

        public IImageServerSupervisor ImageSupervisor { get; } = Substitute.For<IImageServerSupervisor>();

        public WhisperRuntimeActivityGate WhisperGate { get; } = new();

        public FakeWhisperServerSupervisor WhisperSupervisor { get; } = new();
    }
}
