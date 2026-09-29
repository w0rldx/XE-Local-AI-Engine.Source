namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
[Category(TestCategories.Unit)]
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
        var runtime = new StableDiffusionCppRuntime(supervisor, new SdServerJobClient(http), broker, NullLogger<StableDiffusionCppRuntime>.Instance);
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
    ///     sd-server rounds a requested latent grid up to a multiple of 64, so a requested 100x512 comes back as a
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
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress), new SdServerJobClient(http), new ImageServerProgressBroker(), NullLogger<StableDiffusionCppRuntime>.Instance);

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
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress), new SdServerJobClient(http), new ImageServerProgressBroker(), NullLogger<StableDiffusionCppRuntime>.Instance);

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
        var runtime = new StableDiffusionCppRuntime(supervisor, new SdServerJobClient(http), broker, NullLogger<StableDiffusionCppRuntime>.Instance);

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
        var runtime = new StableDiffusionCppRuntime(supervisor, new SdServerJobClient(http), broker, NullLogger<StableDiffusionCppRuntime>.Instance);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), cts.Token));

        AssertEx.True(handler.CancelCalls >= 1, "A generating cancel must first attempt the sd-server cancel route.");
        AssertEx.Equal(expected: 1, supervisor.RestartCount);
    }

    [Test]
    public async Task Generate_JobFailed_ThrowsSanitizedLogsTheDaemonReasonAndEvictsTheDaemon()
    {
        // Tester round 4: a failed job (CUDA OOM after a chat model took the VRAM) left the same broken daemon resident,
        // so every later job failed too, and the log carried no reason at all.
        using var handler = new RuntimeHandler((_, route) => route switch
        {
            "img_gen" => Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}"""),
            _ => Json(HttpStatusCode.OK, """{"status":"failed","error":{"code":"oom","message":"internal-cuda-oom-at-0xdeadbeef\nforged line"}}""")
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var lease = new FakeJobLease();
        var supervisor = new FakeImageServerSupervisor(BaseAddress, lease);
        var logger = new RecordingLogger<StableDiffusionCppRuntime>();
        var runtime = new StableDiffusionCppRuntime(supervisor, new SdServerJobClient(http), new ImageServerProgressBroker(), logger);

        var exception = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), CancellationToken.None));

        AssertEx.False(exception.Message.Contains("0xdeadbeef", StringComparison.Ordinal), "The failure message must be sanitized (no internal detail).");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "(oom): internal-cuda-oom-at-0xdeadbeef forged line"), "The daemon's own reason must reach the log on one line.");
        AssertEx.Equal(expected: 1, supervisor.EvictCount);
        AssertEx.True(lease.DisposedBeforeEvict, "The job lease must be released before the daemon is evicted.");
        AssertEx.Equal(expected: 0, supervisor.RestartCount);
    }

    [Test]
    public void Bound_TruncatesLongForeignErrorText()
    {
        var bounded = StableDiffusionCppRuntime.Bound(new string('x', 1000));

        AssertEx.Equal(expected: 301, bounded!.Length);
        AssertEx.Null(StableDiffusionCppRuntime.Bound("   "));
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
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress), new SdServerJobClient(http), new ImageServerProgressBroker(), NullLogger<StableDiffusionCppRuntime>.Instance);

        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), CancellationToken.None));
    }

    [Test]
    public async Task Generate_DaemonAlreadyDead_FailsNamingTheExitWithoutPollingIt()
    {
        // R8: a SIGKILLed sd-server failed the job only after the poll GET's ~8.5 s of connection-refused retries, as a
        // bare "Image generation failed.". A daemon the supervisor already knows is dead fails the job at once, by name.
        using var handler = new RuntimeHandler((_, route) => route == "img_gen"
            ? Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}""")
            : Status(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var lease = new FakeJobLease
        {
            ExitCode = 137,
            Exited = true
        };
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress, lease), new SdServerJobClient(http), new ImageServerProgressBroker(), NullLogger<StableDiffusionCppRuntime>.Instance);

        var exception = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), CancellationToken.None));

        AssertEx.True(exception.ProcessExited);
        AssertEx.Equal("The image server stopped unexpectedly (exit code 137). It restarts with the next generation.", exception.Message);
        AssertEx.Equal(expected: 0, handler.GetJobCalls);
    }

    [Test]
    public async Task Generate_PollRefusedBecauseTheDaemonDied_FailsNamingTheExit()
    {
        var lease = new FakeJobLease();
        using var handler = new RuntimeHandler((_, route) =>
        {
            if (route == "img_gen")
            {
                return Json(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}""");
            }

            lease.Exited = true;
            throw new HttpRequestException("Connection refused");
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var runtime = new StableDiffusionCppRuntime(new FakeImageServerSupervisor(BaseAddress, lease), new SdServerJobClient(http), new ImageServerProgressBroker(), NullLogger<StableDiffusionCppRuntime>.Instance);

        var exception = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => runtime.GenerateAsync(Request(), new RecordingProgress(), CancellationToken.None));

        AssertEx.True(exception.ProcessExited);
        AssertEx.Equal("The image server stopped unexpectedly. It restarts with the next generation.", exception.Message);
        AssertEx.True(exception.InnerException is HttpRequestException, "The refused poll stays attached as the cause.");
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
    private sealed class FakeImageServerSupervisor : IImageServerSupervisor
    {
        private readonly Uri _baseAddress;
        private readonly IImageServerJobLease? _lease;

        public FakeImageServerSupervisor(Uri baseAddress, IImageServerJobLease? lease = null)
        {
            _baseAddress = baseAddress;
            _lease = lease;
        }

        public int EnsureCount { get; private set; }

        public int RestartCount { get; private set; }

        public int EvictCount { get; private set; }

        public Task<ImageServerEndpoint> EnsureRunningAsync(string modelName, CancellationToken ct)
        {
            EnsureCount++;
            return Task.FromResult(new ImageServerEndpoint
            {
                ModelName = modelName,
                BaseAddress = _baseAddress
            });
        }

        public Task<ImageServerEndpoint> RestartAsync(string modelName, CancellationToken ct)
        {
            RestartCount++;
            return Task.FromResult(new ImageServerEndpoint
            {
                ModelName = modelName,
                BaseAddress = _baseAddress
            });
        }

        public Task EvictAsync(string modelName, CancellationToken ct)
        {
            EvictCount++;
            if (_lease is FakeJobLease { Disposed: true } fake)
            {
                fake.DisposedBeforeEvict = true;
            }

            return Task.CompletedTask;
        }

        public Task<ImageServerEvictAllResult> EvictAllAsync(CancellationToken ct)
        {
            EvictCount++;
            return Task.FromResult(new ImageServerEvictAllResult
            {
                Evicted = true,
                Activity = new ImageRuntimeActivitySnapshot
                {
                    ActiveJobCount = 0,
                    SpawnReadinessCount = 0,
                    ResidentProcessCount = 0,
                    MutationReserved = false,
                    EvictionReserved = false
                }
            });
        }

        // Null unless a test supplies a lease: the runtime then proceeds leaseless, exactly as against a genuinely absent daemon.
        public IImageServerJobLease? TryAcquireJobLease(string modelName)
        {
            return _lease;
        }
    }

    private sealed class FakeJobLease : IImageServerJobLease
    {
        public bool Exited { get; set; }

        public int? ExitCode { get; init; }

        public bool Disposed { get; private set; }

        public bool DisposedBeforeEvict { get; set; }

        public void Touch()
        {
            // Nothing to keep alive: the fake has no idle clock.
        }

        public bool HasDaemonExited(out int? exitCode)
        {
            exitCode = Exited ? ExitCode : null;
            return Exited;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>Routes each sd-server request to <c>img_gen</c> / <c>job</c> / <c>cancel</c> and delegates the response.</summary>
    private sealed class RuntimeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

        public RuntimeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

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

            return Task.FromResult(_responder(request, route));
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
