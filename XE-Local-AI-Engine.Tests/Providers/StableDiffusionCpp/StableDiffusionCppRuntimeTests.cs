namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Net;
using System.Text;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the sd-server runtime adapter's job orchestration against a FAKE sd-server (no real binary): submit →
///     poll → completed decodes the base64 image; a queued-cancel calls the HTTP cancel route; a generating-cancel (409)
///     tree-kills + restarts the daemon; and failed / 410-Gone surface a sanitized error.
/// </summary>
public sealed class StableDiffusionCppRuntimeTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:18200/");

    private static ImageGenerationRequest Request()
    {
        return new ImageGenerationRequest
        {
            ModelName = "sd15",
            Prompt = "a watercolor fox",
            Width = 512,
            Height = 512,
            Steps = 20,
            Seed = -1
        };
    }

    [Test]
    public async Task Generate_SubmitPollCompleted_DecodesBase64Image()
    {
        var pngBytes = new byte[]
        {
            9,
            8,
            7,
            6,
            5,
            4,
            3,
            2,
            1
        };
        var base64 = Convert.ToBase64String(pngBytes);
        using var handler = new RuntimeHandler((_, route) => route switch
        {
            "img_gen" => Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}"""),
            "job" => Json(HttpStatusCode.OK, "{\"status\":\"completed\",\"result\":{\"images\":[{\"b64_json\":\"" + base64 + "\",\"seed\":77}]}}"),
            _ => Status(HttpStatusCode.OK)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var supervisor = new FakeImageServerSupervisor(BaseAddress);
        var broker = new ImageServerProgressBroker();
        var runtime = new StableDiffusionCppRuntime(supervisor, new SdServerJobClient(http), broker);
        var progress = new RecordingProgress();

        var result = await runtime.GenerateAsync(Request(), progress, CancellationToken.None);

        AssertEx.True(result.ImageBytes.Span.SequenceEqual(pngBytes), "The decoded image must match the base64 payload.");
        AssertEx.Equal(expected: 77L, result.Seed);
        AssertEx.Equal("png", result.Format);
        AssertEx.Equal(expected: 1, supervisor.EnsureCount);
        AssertEx.Equal(expected: 0, supervisor.RestartCount);
        AssertEx.Contains(progress.Reports, report => report.Phase == ImageGenPhase.Completed);
    }

    /// <summary>
    ///     F-030: sd-server rounds a requested latent grid up to a multiple of 64, so a requested 100x512 comes back as a
    ///     128x512 PNG. The result must describe the bytes that arrived, not the request that was sent — echoing the
    ///     request made the job card state a false fact about its own output.
    /// </summary>
    [Test]
    public async Task Generate_RuntimeRoundedTheSize_ReportsTheProducedDimensionsNotTheRequestedOnes()
    {
        var png = PngWithHeader(width: 128, height: 512);
        var base64 = Convert.ToBase64String(png);
        using var handler = new RuntimeHandler((_, route) => route switch
        {
            "img_gen" => Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}"""),
            _ => Json(HttpStatusCode.OK, "{\"status\":\"completed\",\"result\":{\"images\":[{\"b64_json\":\"" + base64 + "\",\"seed\":7}]}}")
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress), new SdServerJobClient(http), new ImageServerProgressBroker());

        var request = Request() with
        {
            Width = 100,
            Height = 512
        };
        var result = await runtime.GenerateAsync(request, new RecordingProgress(), CancellationToken.None);

        AssertEx.Equal(expected: 128, result.Width, "The result must report the produced width (128), not the requested one (100).");
        AssertEx.Equal(expected: 512, result.Height);
    }

    /// <summary>A payload with no readable PNG header falls back to the requested size rather than reporting nonsense.</summary>
    [Test]
    public async Task Generate_UnreadableImageHeader_FallsBackToTheRequestedDimensions()
    {
        var base64 = Convert.ToBase64String(new byte[]
        {
            1,
            2,
            3,
            4
        });
        using var handler = new RuntimeHandler((_, route) => route switch
        {
            "img_gen" => Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}"""),
            _ => Json(HttpStatusCode.OK, "{\"status\":\"completed\",\"result\":{\"images\":[{\"b64_json\":\"" + base64 + "\",\"seed\":7}]}}")
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress), new SdServerJobClient(http), new ImageServerProgressBroker());

        var result = await runtime.GenerateAsync(Request() with
        {
            Width = 100,
            Height = 512
        }, new RecordingProgress(), CancellationToken.None);

        AssertEx.Equal(expected: 100, result.Width);
        AssertEx.Equal(expected: 512, result.Height);
    }

    // A minimal but structurally valid PNG prefix: 8-byte signature, then the IHDR chunk whose first two fields are the
    // big-endian width and height. Only the header is read, so no pixel data is needed.
    private static byte[] PngWithHeader(uint width, uint height)
    {
        var bytes = new List<byte>(new byte[]
        {
            0x89,
            0x50,
            0x4E,
            0x47,
            0x0D,
            0x0A,
            0x1A,
            0x0A
        });
        bytes.AddRange([0x00, 0x00, 0x00, 0x0D]);
        bytes.AddRange([0x49, 0x48, 0x44, 0x52]);
        bytes.AddRange(BitConverter.GetBytes(width).Reverse());
        bytes.AddRange(BitConverter.GetBytes(height).Reverse());

        // Bit depth / colour type / compression / filter / interlace — present so the chunk is well-formed.
        bytes.AddRange([0x08, 0x02, 0x00, 0x00, 0x00]);
        return bytes.ToArray();
    }

    [Test]
    public async Task Generate_CancelWhileQueued_CallsHttpCancel_NoRestart()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new RuntimeHandler((_, route) =>
        {
            switch (route)
            {
                case "img_gen":
                    return Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}""");
                case "job":
                    // Cancellation arrives while the job is still queued.
                    cts.Cancel();
                    return Json(HttpStatusCode.OK, """{"status":"queued","queue_position":2}""");
                default:
                    // Queued jobs cancel cleanly (200).
                    return Status(HttpStatusCode.OK);
            }
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var supervisor = new FakeImageServerSupervisor(BaseAddress);
        var broker = new ImageServerProgressBroker();
        var runtime = new StableDiffusionCppRuntime(supervisor, new SdServerJobClient(http), broker);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), cts.Token));

        AssertEx.True(handler.CancelCalls >= 1, "A queued cancel must POST the sd-server cancel route.");
        AssertEx.Equal(expected: 0, supervisor.RestartCount);
    }

    [Test]
    public async Task Generate_CancelWhileGenerating_TreeKillsAndRestartsDaemon()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new RuntimeHandler((_, route) =>
        {
            switch (route)
            {
                case "img_gen":
                    return Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}""");
                case "job":
                    // Cancellation arrives after generation has begun.
                    cts.Cancel();
                    return Json(HttpStatusCode.OK, """{"status":"generating"}""");
                default:
                    // A generating job cannot be interrupted over HTTP (409).
                    return Status(HttpStatusCode.Conflict);
            }
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var supervisor = new FakeImageServerSupervisor(BaseAddress);
        var broker = new ImageServerProgressBroker();
        var runtime = new StableDiffusionCppRuntime(supervisor, new SdServerJobClient(http), broker);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), cts.Token));

        AssertEx.True(handler.CancelCalls >= 1, "A generating cancel must first attempt the sd-server cancel route.");
        AssertEx.Equal(expected: 1, supervisor.RestartCount);
    }

    [Test]
    public async Task Generate_JobFailed_ThrowsSanitized()
    {
        using var handler = new RuntimeHandler((_, route) => route switch
        {
            "img_gen" => Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}"""),
            _ => Json(HttpStatusCode.OK, """{"status":"failed","error":{"code":"oom","message":"internal-cuda-oom-at-0xdeadbeef"}}""")
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress), new SdServerJobClient(http), new ImageServerProgressBroker());

        var exception = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), CancellationToken.None));

        AssertEx.False(exception.Message.Contains("0xdeadbeef", StringComparison.Ordinal), "The failure message must be sanitized (no internal detail).");
    }

    [Test]
    public async Task Generate_JobExpired410_ThrowsSanitized()
    {
        using var handler = new RuntimeHandler((_, route) => route switch
        {
            "img_gen" => Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}"""),
            _ => Status(HttpStatusCode.Gone)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress), new SdServerJobClient(http), new ImageServerProgressBroker());

        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), CancellationToken.None));
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage Status(HttpStatusCode code)
    {
        return new HttpResponseMessage(code);
    }

    /// <summary>Fake supervisor: returns a fixed endpoint and records ensure/restart/evict calls; never spawns a process.</summary>
    private sealed class FakeImageServerSupervisor(Uri baseAddress) : IImageServerSupervisor
    {
        public int EnsureCount { get; private set; }

        public int RestartCount { get; private set; }

        public int EvictCount { get; private set; }

        public Task<ImageServerEndpoint> EnsureRunningAsync(string modelName, CancellationToken ct)
        {
            EnsureCount++;
            return Task.FromResult(new ImageServerEndpoint(modelName, baseAddress));
        }

        public Task<ImageServerEndpoint> RestartAsync(string modelName, CancellationToken ct)
        {
            RestartCount++;
            return Task.FromResult(new ImageServerEndpoint(modelName, baseAddress));
        }

        public Task EvictAsync(string modelName, CancellationToken ct)
        {
            EvictCount++;
            return Task.CompletedTask;
        }

        public Task<ImageServerEvictAllResult> EvictAllAsync(CancellationToken ct)
        {
            EvictCount++;
            return Task.FromResult(new ImageServerEvictAllResult(true,
                new ImageRuntimeActivitySnapshot(0, 0, 0, MutationReserved: false, EvictionReserved: false)));
        }

        // The runtime now acquires a job lease across submit→poll→complete. This fake has no resident daemon
        // to lease, so it returns null — the runtime then proceeds leaseless, exactly as it does against a genuinely
        // absent daemon, keeping these runtime tests behaviour-identical.
        public IImageServerJobLease? TryAcquireJobLease(string modelName)
        {
            return null;
        }
    }

    /// <summary>Routes each sd-server request to <c>img_gen</c> / <c>job</c> / <c>cancel</c> and delegates the response.</summary>
    private sealed class RuntimeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int ImgGenCalls { get; private set; }

        public int GetJobCalls { get; private set; }

        public int CancelCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string route;
            if (path.EndsWith("/img_gen", StringComparison.Ordinal))
            {
                ImgGenCalls++;
                route = "img_gen";
            }
            else if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                CancelCalls++;
                route = "cancel";
            }
            else
            {
                GetJobCalls++;
                route = "job";
            }

            return Task.FromResult(responder(request, route));
        }
    }

    private sealed class RecordingProgress : IProgress<ImageGenProgress>
    {
        public List<ImageGenProgress> Reports { get; } = [];

        public void Report(ImageGenProgress value)
        {
            Reports.Add(value);
        }
    }
}
