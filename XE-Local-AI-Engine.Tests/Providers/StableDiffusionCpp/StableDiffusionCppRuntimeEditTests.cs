namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Edit modes against a fake sd-server: the img_gen body carries init_image/strength or ref_images, and the build's
///     capabilities gate the job, failing closed.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class StableDiffusionCppRuntimeEditTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:18200/");

    private static readonly byte[] SourceBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 42, 43, 44];

    private static readonly string SourceBase64 = Convert.ToBase64String(SourceBytes);

    private const string AllFeatures = """{"supported_modes":["img_gen"],"features_by_mode":{"img_gen":{"init_image":true,"mask_image":true,"ref_images":true}}}""";

    private static ImageGenerationRequest Request(ImageEditMode? mode = null, double? strength = null)
    {
        return new ImageGenerationRequest
        {
            ModelName = "sd15",
            Prompt = "a watercolor fox",
            Mode = mode,
            InitImage = mode == ImageEditMode.Img2Img ? SourceBytes : null,
            ReferenceImage = mode == ImageEditMode.Reference ? SourceBytes : null,
            Strength = strength
        };
    }

    [Test]
    public async Task Img2Img_SubmitsInitImageAndStrength_NoRefImages()
    {
        using var handler = new EditHandler(AllFeatures);
        using var http = new HttpClient(handler, disposeHandler: false);

        await Runtime(http).GenerateAsync(Request(ImageEditMode.Img2Img, strength: 0.4), new Progress<ImageGenProgress>(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.ImgGenBody!);
        AssertEx.Equal(SourceBase64, body.RootElement.GetProperty("init_image").GetString());
        AssertEx.Equal(expected: 0.4, body.RootElement.GetProperty("strength").GetDouble());
        AssertEx.False(body.RootElement.TryGetProperty("ref_images", out _), "img2img must not send ref_images.");
        AssertEx.Equal(expected: 1, handler.CapabilitiesCalls);
    }

    [Test]
    public async Task Img2Img_NoStrength_SendsTheDefault()
    {
        using var handler = new EditHandler(AllFeatures);
        using var http = new HttpClient(handler, disposeHandler: false);

        await Runtime(http).GenerateAsync(Request(ImageEditMode.Img2Img), new Progress<ImageGenProgress>(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.ImgGenBody!);
        AssertEx.Equal(expected: 0.75, body.RootElement.GetProperty("strength").GetDouble());
    }

    [Test]
    public async Task Reference_SubmitsOneRefImage_NoInitImageOrStrength()
    {
        using var handler = new EditHandler(AllFeatures);
        using var http = new HttpClient(handler, disposeHandler: false);

        await Runtime(http).GenerateAsync(Request(ImageEditMode.Reference, strength: 0.4), new Progress<ImageGenProgress>(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.ImgGenBody!);
        var refs = body.RootElement.GetProperty("ref_images");
        AssertEx.Equal(expected: 1, refs.GetArrayLength());
        AssertEx.Equal(SourceBase64, refs[0].GetString());
        AssertEx.False(body.RootElement.TryGetProperty("init_image", out _), "reference must not send init_image.");
        AssertEx.False(body.RootElement.TryGetProperty("strength", out _), "reference must not send strength.");
    }

    [Test]
    public async Task TextToImage_SendsNoEditKeys_AndNeverAsksForCapabilities()
    {
        using var handler = new EditHandler(AllFeatures);
        using var http = new HttpClient(handler, disposeHandler: false);

        await Runtime(http).GenerateAsync(Request(), new Progress<ImageGenProgress>(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.ImgGenBody!);
        foreach (var key in new[]
                 {
                     "init_image",
                     "ref_images",
                     "strength"
                 })
        {
            AssertEx.False(body.RootElement.TryGetProperty(key, out _), $"txt2img must not send {key}.");
        }

        AssertEx.Equal(expected: 0, handler.CapabilitiesCalls);
    }

    [Test]
    [Arguments(ImageEditMode.Img2Img, """{"features_by_mode":{"img_gen":{"init_image":false,"ref_images":true}}}""")]
    [Arguments(ImageEditMode.Reference, """{"features_by_mode":{"img_gen":{"init_image":true}}}""")]
    [Arguments(ImageEditMode.Img2Img, """{"supported_modes":["img_gen"]}""")]
    [Arguments(ImageEditMode.Img2Img, "not json")]
    [Arguments(ImageEditMode.Reference, """{"features_by_mode":{"img_gen":{"ref_images":"yes"}}}""")]
    [Arguments(ImageEditMode.Img2Img, null)]
    public async Task EditMode_UnsupportedOrUnknownCapabilities_FailsClosedWithoutSubmitting(ImageEditMode mode, string? capabilitiesJson)
    {
        // A null body stands for a 500 from the capabilities route.
        using var handler = new EditHandler(capabilitiesJson);
        using var http = new HttpClient(handler, disposeHandler: false);

        var exception = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() =>
            Runtime(http).GenerateAsync(Request(mode), new Progress<ImageGenProgress>(), CancellationToken.None));

        AssertEx.True(exception.FeatureUnsupported);
        AssertEx.Equal("The image runtime build does not support this edit mode.", exception.Message);
        AssertEx.False(exception.ToString().Contains(SourceBase64, StringComparison.Ordinal), "The source bytes must never reach an exception.");
        AssertEx.Equal(expected: 0, handler.ImgGenCalls);
    }

    [Test]
    [Arguments(ImageEditMode.Img2Img)]
    [Arguments(ImageEditMode.Reference)]
    public async Task EditMode_WithoutSourceBytes_IsACallerBug_NothingSent(ImageEditMode mode)
    {
        using var handler = new EditHandler(AllFeatures);
        using var http = new HttpClient(handler, disposeHandler: false);
        var supervisor = Supervisor();
        var runtime = Runtime(http, supervisor);
        var request = Request(mode) with
        {
            InitImage = null,
            ReferenceImage = mode == ImageEditMode.Img2Img ? SourceBytes : null
        };

        var exception = await AssertEx.ThrowsAsync<ArgumentException>(() => runtime.GenerateAsync(request, new Progress<ImageGenProgress>(), CancellationToken.None));

        AssertEx.False(exception.Message.Contains(SourceBase64, StringComparison.Ordinal), "The source bytes must never reach an exception.");
        AssertEx.Equal(expected: 0, handler.ImgGenCalls + handler.CapabilitiesCalls);
        await supervisor.DidNotReceiveWithAnyArgs().EnsureRunningAsync(default!, default);
    }

    private static IImageServerSupervisor Supervisor()
    {
        var supervisor = Substitute.For<IImageServerSupervisor>();
        supervisor.EnsureRunningAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(call => new ImageServerEndpoint
                  {
                      ModelName = call.Arg<string>(),
                      BaseAddress = BaseAddress
                  });
        supervisor.TryAcquireJobLease(Arg.Any<string>()).Returns((IImageServerJobLease?)null);
        return supervisor;
    }

    private static StableDiffusionCppRuntime Runtime(HttpClient http, IImageServerSupervisor? supervisor = null)
    {
        return new StableDiffusionCppRuntime(supervisor ?? Supervisor(), new SdServerJobClient(http), new ImageServerProgressBroker(),
            NullLogger<StableDiffusionCppRuntime>.Instance);
    }

    /// <summary>Fake sd-server: canned capabilities (null = HTTP 500), records the img_gen body, completes every job at once.</summary>
    private sealed class EditHandler : HttpMessageHandler
    {
        private readonly string? _capabilitiesJson;

        public EditHandler(string? capabilitiesJson)
        {
            _capabilitiesJson = capabilitiesJson;
        }

        public int CapabilitiesCalls { get; private set; }

        public int ImgGenCalls { get; private set; }

        public string? ImgGenBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
            {
                CapabilitiesCalls++;
                return _capabilitiesJson is null ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json(_capabilitiesJson);
            }

            if (path.EndsWith("/img_gen", StringComparison.Ordinal))
            {
                ImgGenCalls++;
                ImgGenBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("""{"id":"job-1","status":"queued"}""");
            }

            return Json("""{"status":"completed","result":{"images":[{"b64_json":"AQID","seed":7}]}}""");
        }

        private static HttpResponseMessage Json(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
