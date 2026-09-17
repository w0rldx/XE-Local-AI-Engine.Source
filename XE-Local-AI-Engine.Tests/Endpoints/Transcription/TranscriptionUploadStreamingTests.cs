namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Redirects ASP.NET Core's buffered-form spill directory for the whole test session, and names it so the
///     streaming tests can watch it.
/// </summary>
/// <remarks>
///     It has to be the session, not one class. <c>AspNetCoreTempDirectory</c> reads <c>ASPNETCORE_TEMP</c> once and
///     caches it in a static for the life of the process, on the FIRST spill — so a class-level hook would be silently
///     ignored whenever any earlier test in the run had already buffered a form file, and the sentinel would then be
///     watching a directory that nothing writes to. TUnit requires a global hook to live in a class of its own.
/// </remarks>
public static class FrameworkTempSentinel
{
    private const string TempEnvironmentVariable = "ASPNETCORE_TEMP";

    private static string? _previousTempDirectory;

    /// <summary>
    ///     Where ASP.NET Core spills buffered form files once the hook below has pointed it here.
    ///     <para>
    ///         Named per OS PROCESS, because <c>[NotInParallel]</c> — keyed or bare — only serializes tests that one
    ///         process's own execution engine scheduled. The runner gives each namespace a process of its own and runs
    ///         several at once, all sharing the box's temp root, so a fixed path here lets a sibling process's spill
    ///         land in the directory this one is asserting is empty. The prefix stays so the directory is still
    ///         recognisable as this suite's, and <see cref="Restore" /> still deletes the one it made.
    ///     </para>
    /// </summary>
    public static string Directory { get; } =
        Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-framework-temp-{Environment.ProcessId}");

    /// <summary>The spill files present right now; a buffered upload larger than 64 KB puts one here while it runs.</summary>
    public static IReadOnlyList<string> Files() =>
        System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory) : [];

    [Before(TestSession)]
    public static void Redirect()
    {
        _previousTempDirectory = Environment.GetEnvironmentVariable(TempEnvironmentVariable);
        _ = System.IO.Directory.CreateDirectory(Directory);
        Environment.SetEnvironmentVariable(TempEnvironmentVariable, Directory);
    }

    [After(TestSession)]
    public static void Restore()
    {
        Environment.SetEnvironmentVariable(TempEnvironmentVariable, _previousTempDirectory);
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A scratch directory that outlives the run is litter, never a failure.
        }
    }
}

/// <summary>
///     The file that must never exist. A bound <see cref="IFormFile" /> spills everything past the 64 KB memory
///     threshold into a framework-owned temp file under <c>ASPNETCORE_TEMP</c> before the handler runs; the upload
///     endpoint streams the multipart section instead, so nothing is ever written there.
/// </summary>
/// <remarks>
///     <para>
///         <b>Checking the directory afterwards would prove nothing.</b> <c>FileBufferingReadStream</c> deletes its
///         spill file when it is disposed, which happens at the end of every request, so a buffered implementation and
///         a streaming one leave an identical empty directory behind. Every test here therefore holds the request open
///         on a gate past the point where a spill would have happened, and looks THEN.
///     </para>
///     <para>
///         <b><see cref="BufferedControlEndpoint_WhileRequestActive_DoesSpillToFrameworkTemp" /> is what makes the
///         rest evidence.</b> It drives a deliberately buffered route with the same payload behind the same gate and
///         requires the sentinel to be non-empty. If that one ever goes green-empty, the sentinel is not watching the
///         directory the framework uses and every other assertion in this class is vacuous.
///     </para>
/// </remarks>
[NotInParallel(FrameworkTempSentinelKey)]
[Category(TestCategories.Integration)]
public sealed class TranscriptionUploadStreamingTests
{
    /// <summary>
    ///     Shared with <c>ConversationUploadEndpointTests</c>, the only other class in this assembly that posts a
    ///     multipart file past the spill threshold. The sentinel directory is process-wide, so a concurrent spill from
    ///     anywhere else IN THIS PROCESS would read as this endpoint's.
    ///     <para>
    ///         It is the whole guard only when the runner schedules both classes into one process. Across processes
    ///         nothing here reaches, which is why <see cref="FrameworkTempSentinel.Directory" /> carries the process id.
    ///     </para>
    /// </summary>
    internal const string FrameworkTempSentinelKey = "framework-temp-sentinel";

    private const string ApiPrefix = "/api/local/v1";

    private const string BufferedControlRoute = "/transcription-buffered-control";

    /// <summary>
    ///     The shared contended budget rather than a local number. Every wait it bounds is a failure deadline on
    ///     something expected within milliseconds — a gate completing, a directory going empty — so a green run
    ///     returns at once and only a genuinely stuck one spends it. None of them is a "stays empty" window, which
    ///     is the one shape widening would weaken.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TestBudgets.Contended;

    [Test]
    public async Task Upload_WhileRequestActive_LeavesNoFrameworkTempFile()
    {
        await AssertNoFrameworkSpillEventuallyAsync("before the test starts").ConfigureAwait(false);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new StubTranscriptionService
        {
            Gate = gate
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var request = UploadRequest(factory, service);
        try
        {
            var send = client.SendAsync(request);
            await AwaitHandlerAsync(service).ConfigureAwait(false);

            // The whole body has been consumed by now — the handler only reaches the transcription step after the
            // copy — so a buffered implementation's spill file would exist at exactly this moment.
            AssertNoFrameworkSpill("while the request is still in flight");

            gate.SetResult();
            using var response = await send.ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            request.Dispose();
        }

        await AssertNoFrameworkSpillEventuallyAsync("after the response").ConfigureAwait(false);
        AssertOwnedFilesDeleted(service);
    }

    [Test]
    public async Task BufferedControlEndpoint_WhileRequestActive_DoesSpillToFrameworkTemp()
    {
        await AssertNoFrameworkSpillEventuallyAsync("before the test starts").ConfigureAwait(false);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWithBufferedControl(service, gate, entered);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, BufferedControlRoute)
        {
            Content = BuildForm()
        };
        try
        {
            var send = client.SendAsync(request);
            await AssertEx.CompletesAsync(entered.Task, GateTimeout, "The buffered control route must finish reading the form.")
                          .ConfigureAwait(false);

            AssertEx.NotEmpty(FrameworkTempSentinel.Files(),
                "The buffered control must spill to the sentinel directory. An empty one here means the sentinel is "
                + "not watching the directory the framework uses, and every other test in this class proves nothing.");

            gate.SetResult();
            using var response = await send.ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            request.Dispose();
        }

        // And the spill is gone once the request ends — which is exactly why "empty afterwards" is not evidence.
        await AssertNoFrameworkSpillEventuallyAsync("after the buffered request ended").ConfigureAwait(false);
    }

    [Test]
    public async Task Upload_WhenRejected_LeavesNoFrameworkTempFile()
    {
        await AssertNoFrameworkSpillEventuallyAsync("before the test starts").ConfigureAwait(false);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new StubTranscriptionService
        {
            Gate = gate,
            TranscribeResult = TranscribeFileResult.UnsupportedContainer(AudioContainer.Ogg,
                ["wav", "mp3", "flac"],
                ffmpegRequired: true,
                "Ogg audio needs ffmpeg, which is not installed on this node.")
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var request = UploadRequest(factory, service);
        try
        {
            var send = client.SendAsync(request);
            await AwaitHandlerAsync(service).ConfigureAwait(false);
            AssertNoFrameworkSpill("while a refused request is still in flight");

            gate.SetResult();
            using var response = await send.ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }
        finally
        {
            request.Dispose();
        }

        await AssertNoFrameworkSpillEventuallyAsync("after the refusal").ConfigureAwait(false);
        AssertOwnedFilesDeleted(service);
    }

    [Test]
    public async Task Upload_WhenCancelled_LeavesNoFrameworkTempFile()
    {
        await AssertNoFrameworkSpillEventuallyAsync("before the test starts").ConfigureAwait(false);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new StubTranscriptionService
        {
            Gate = gate
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var request = UploadRequest(factory, service);
        using var abort = new CancellationTokenSource();
        try
        {
            var send = client.SendAsync(request, abort.Token);
            await AwaitHandlerAsync(service).ConfigureAwait(false);
            AssertNoFrameworkSpill("while the request the client is about to abandon is in flight");

            await abort.CancelAsync().ConfigureAwait(false);
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => send).ConfigureAwait(false);
        }
        finally
        {
            _ = gate.TrySetResult();
            request.Dispose();
        }

        // The handler unwinds through the same await using every other ending goes through. Eventually, because the
        // client stops waiting the moment it cancels, while the server-side unwind is still a step behind it.
        await AssertEx.EventuallyAsync(() => OwnedFiles(service).Count == 0,
                          GateTimeout,
                          "A cancelled upload must leave no engine-owned audio behind.")
                      .ConfigureAwait(false);
        await AssertNoFrameworkSpillEventuallyAsync("after the client abandoned the request").ConfigureAwait(false);
    }

    [Test]
    public async Task Upload_WhenHandlerThrows_LeavesNoFrameworkTempFile()
    {
        await AssertNoFrameworkSpillEventuallyAsync("before the test starts").ConfigureAwait(false);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new StubTranscriptionService
        {
            Gate = gate,
            TranscribeThrows = new InvalidOperationException("the transcription step blew up")
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var request = UploadRequest(factory, service);
        try
        {
            var send = client.SendAsync(request);
            await AwaitHandlerAsync(service).ConfigureAwait(false);
            AssertNoFrameworkSpill("while the request that is about to fail is in flight");

            gate.SetResult();
            using var response = await send.ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            request.Dispose();
        }

        await AssertNoFrameworkSpillEventuallyAsync("after the handler threw").ConfigureAwait(false);
        AssertOwnedFilesDeleted(service);
    }

    private static Task AwaitHandlerAsync(StubTranscriptionService service) =>
        AssertEx.CompletesAsync(service.Entered, GateTimeout, "The upload handler must reach the transcription step.");

    /// <summary>
    ///     The committed 352 KB clip — comfortably past the 64 KB <c>FormOptions.MemoryBufferThreshold</c> a buffered
    ///     form file spills at. A payload at or under that threshold would never spill, so the size is load-bearing.
    /// </summary>
    private static MultipartFormDataContent BuildForm()
    {
        var payload = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcription", "jfk.wav"));
        ByteArrayContent? fileContent = null;
        MultipartFormDataContent? form = null;
        try
        {
            fileContent = new ByteArrayContent(payload);
            form = new MultipartFormDataContent();
            form.Add(fileContent, "file", "clip.wav");

            // Ownership moves to the form, which moves to the request: disposing the request disposes all three.
            var built = form;
            form = null;
            fileContent = null;
            return built;
        }
        finally
        {
            form?.Dispose();
            fileContent?.Dispose();
        }
    }

    /// <summary>
    ///     Posts to a session the stub already holds: the endpoint refuses an unknown id before reading a byte, so a
    ///     throwaway Guid here would 404 and never reach the copy these tests are about.
    /// </summary>
    private static HttpRequestMessage UploadRequest(TestServerWebAppFactory factory, StubTranscriptionService service)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{service.SeedSession()}/file")
        {
            Content = BuildForm()
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static void AssertNoFrameworkSpill(string moment) =>
        AssertEx.Empty(FrameworkTempSentinel.Files(), $"No framework-owned copy of the audio may exist {moment}.");

    /// <summary>
    ///     The same check, allowed to settle. The framework deletes a spill file when the request's form feature is
    ///     disposed, which happens after the client already has its response — so an immediate check at that moment
    ///     races the server's own teardown rather than testing anything. The in-flight check above is the evidence.
    /// </summary>
    private static Task AssertNoFrameworkSpillEventuallyAsync(string moment) =>
        AssertEx.EventuallyAsync(() => FrameworkTempSentinel.Files().Count == 0,
            GateTimeout,
            $"No framework-owned copy of the audio may remain {moment}.");

    private static IReadOnlyList<string> OwnedFiles(StubTranscriptionService service) =>
        Directory.Exists(service.UploadDirectory) ? Directory.GetFiles(service.UploadDirectory) : [];

    private static void AssertOwnedFilesDeleted(StubTranscriptionService service) =>
        AssertEx.Empty(OwnedFiles(service), "The upload slot must have deleted every file it owned.");

    private static TestServerWebAppFactory FactoryWith(ITranscriptionService service) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITranscriptionService>();
                services.AddSingleton(service);
            }
        };

    private static TestServerWebAppFactory FactoryWithBufferedControl(ITranscriptionService service,
        TaskCompletionSource gate,
        TaskCompletionSource entered) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITranscriptionService>();
                services.AddSingleton(service);
                services.AddSingleton<IStartupFilter>(new BufferedControlStartupFilter(gate, entered));
            }
        };

    /// <summary>
    ///     Adds the buffered route the design rejects. It reads the form the way a bound <see cref="IFormFile" />
    ///     does — that call IS the spill — and then parks on the same gate, so the sentinel can be inspected while the
    ///     spill file is still on disk. A startup filter is the seam because the test factory exposes services only,
    ///     and middleware a filter adds runs ahead of the node's own pipeline, so the route needs no authentication.
    /// </summary>
    private sealed class BufferedControlStartupFilter(TaskCompletionSource gate, TaskCompletionSource entered) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                _ = app.Use(async (context, nextMiddleware) =>
                {
                    if (!string.Equals(context.Request.Path.Value, BufferedControlRoute, StringComparison.Ordinal))
                    {
                        await nextMiddleware().ConfigureAwait(false);
                        return;
                    }

                    _ = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
                    _ = entered.TrySetResult();
                    await gate.Task.ConfigureAwait(false);
                    context.Response.StatusCode = StatusCodes.Status200OK;
                });

                next(app);
            };
    }
}
