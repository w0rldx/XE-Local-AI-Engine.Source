namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Transcription;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     The upload route end to end against the REAL <see cref="ITranscriptionService" />, with only the runtime seams
///     faked: the size cap, the missing-file refusal, the typed 415 for a container this node cannot decode, and the
///     guarantee that a traversal-laden client file name never reaches a path.
/// </summary>
public sealed class TranscriptionUploadEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Upload_WhenOversize_Rejects()
    {
        // Drop the cap to 1 MB for this host so the test ships 2 MB rather than 26. The cap is enforced DURING the
        // copy — a streamed body declares no length up front — so the refusal must arrive without the bytes landing.
        var transcriber = new FakeWhisperTranscriber();
        await using var factory = FactoryWith(transcriber,
            extraServices: services => services.Configure<SecurityOptions>(options => options.MaxUploadFileSizeMb = 1));
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(factory, client).ConfigureAwait(false);
        using var response = await UploadAsync(factory, client, sessionId, "big.wav", new byte[2 * 1024 * 1024]).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, transcriber.CallCount, "An oversize upload must never reach the runtime.");
        AssertEmptyUploadDirectory(factory);
    }

    [Test]
    public async Task Upload_WhenNoFile_Rejects()
    {
        var transcriber = new FakeWhisperTranscriber();
        await using var factory = FactoryWith(transcriber);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(factory, client).ConfigureAwait(false);

        using var form = new MultipartFormDataContent();
        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/file");
        request.Content = form;
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal(expected: 0, transcriber.CallCount);
    }

    [Test]
    public async Task Upload_WhenContainerUnsupported_Returns415WithSupportedList()
    {
        // No ffmpeg on this node, so an Ogg is a container it cannot decode at all. The refusal has to name what it
        // CAN take and whether installing ffmpeg would fix it — otherwise the operator only learns that it failed.
        var transcriber = new FakeWhisperTranscriber();
        await using var factory = FactoryWith(transcriber, transcoderAvailable: false);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(factory, client).ConfigureAwait(false);
        using var response = await UploadAsync(factory, client, sessionId, "podcast.ogg", TranscriptionAudioFixtures.Ogg).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        var body = await ReadJsonAsync(response).ConfigureAwait(false);
        AssertEx.Equal("ffmpeg-required", body.GetProperty("reason").GetString());
        AssertEx.Equal("Ogg", body.GetProperty("detectedContainer").GetString());
        AssertEx.True(body.GetProperty("ffmpegRequired").GetBoolean());
        AssertEx.Contains(body.GetProperty("message").GetString(), "ffmpeg");

        var supported = body.GetProperty("supportedContainers").EnumerateArray().Select(static entry => entry.GetString()).ToArray();
        AssertEx.Contains(supported, "wav");
        AssertEx.Contains(supported, "mp3");
        AssertEx.Contains(supported, "flac");
        AssertEx.False(supported.Contains("ogg"), "Without ffmpeg the node must not advertise the transcoded containers.");

        AssertEx.Equal(expected: 0, transcriber.CallCount, "A refused container must never reach the runtime.");
        AssertEmptyUploadDirectory(factory);
    }

    [Test]
    public async Task Upload_WhenFileNameCarriesTraversal_IsSanitized()
    {
        var transcriber = new FakeWhisperTranscriber();
        await using var factory = FactoryWith(transcriber);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(factory, client).ConfigureAwait(false);
        var root = factory.Services.GetRequiredService<INodeDataDirectory>().Root;

        using var response = await UploadAsync(factory, client, sessionId, "../../../escaped.wav", ReadFixtureWav()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 1, transcriber.CallCount);

        // The client string contributed nothing but an extension: the slot names the file itself. The concrete proof
        // is that the path the traversal pointed at — two levels above the upload directory — was never created.
        var traversalTarget = Path.GetFullPath(Path.Combine(root, "tmp", "transcription", "..", "..", "..", "escaped.wav"));
        AssertEx.False(File.Exists(traversalTarget), $"Nothing may be written at '{traversalTarget}'.");
        AssertEmptyUploadDirectory(factory);

        var body = await ReadJsonAsync(response).ConfigureAwait(false);
        AssertEx.Equal("Completed", body.GetProperty("session").GetProperty("status").GetString());
    }

    [Test]
    public async Task Upload_WhenFileNameIsNotAName_Rejects()
    {
        // ".." sanitizes to nothing at all. Rejecting is the only safe answer: deriving an extension from it would
        // hand a client-authored string to Path.Combine.
        var transcriber = new FakeWhisperTranscriber();
        await using var factory = FactoryWith(transcriber);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(factory, client).ConfigureAwait(false);
        using var response = await UploadAsync(factory, client, sessionId, "..", ReadFixtureWav()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, transcriber.CallCount);
    }

    /// <summary>
    ///     A body that stops in the middle of the file part is a client error, not a server fault.
    /// </summary>
    /// <remarks>
    ///     The truncation is only discovered while the section is being COPIED — the part header parsed fine — so it
    ///     surfaces from the copy rather than from the section read, outside the arm that catches a malformed document.
    ///     Left unmapped it answers 500 for a client that hung up mid-upload, and a 500 is the one answer a client
    ///     retries against a node that is working perfectly.
    /// </remarks>
    [Test]
    public async Task Upload_WhenBodyTruncatedInsideTheFileSection_Returns400()
    {
        var transcriber = new FakeWhisperTranscriber();
        await using var factory = FactoryWith(transcriber);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(factory, client).ConfigureAwait(false);

        // A well-formed part header followed by bytes and then nothing: no closing boundary ever arrives.
        const string boundary = "xe-truncated-boundary";
        var body = Encoding.ASCII.GetBytes($"--{boundary}\r\n"
                                           + "Content-Disposition: form-data; name=\"file\"; filename=\"clip.wav\"\r\n"
                                           + "Content-Type: application/octet-stream\r\n\r\n"
                                           + "RIFF....WAVEfmt ");

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/file");
        request.Content = content;
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal(expected: 0, transcriber.CallCount, "A truncated body must never reach the runtime.");
        AssertEmptyUploadDirectory(factory);
    }

    /// <summary>
    ///     Two files in one form are refused, rather than one of them being transcribed and the other dropped.
    /// </summary>
    /// <remarks>
    ///     The endpoint reads one file section and transcribes it. Reading no further made a two-file form succeed with
    ///     a transcript of whichever part the client happened to serialize first — an answer that looks correct and is
    ///     silently wrong about half the time.
    /// </remarks>
    [Test]
    public async Task Upload_WithTwoFileParts_Returns400()
    {
        var transcriber = new FakeWhisperTranscriber();
        await using var factory = FactoryWith(transcriber);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(factory, client).ConfigureAwait(false);

        using var form = new MultipartFormDataContent();
        using var first = new ByteArrayContent(ReadFixtureWav());
        using var second = new ByteArrayContent(ReadFixtureWav());
        first.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        second.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(first, "file", "first.wav");
        form.Add(second, "extra", "second.wav");

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/file");
        request.Content = form;
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal(expected: 0, transcriber.CallCount, "Neither file may be transcribed when the request is ambiguous.");
        AssertEmptyUploadDirectory(factory);
    }

    [Test]
    public async Task Upload_WhenSessionIsUnknown_ReturnsNotFound()
    {
        var transcriber = new FakeWhisperTranscriber();
        await using var factory = FactoryWith(transcriber);
        using var client = factory.CreateClient();

        using var response = await UploadAsync(factory, client, Guid.NewGuid(), "clip.wav", ReadFixtureWav()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEmptyUploadDirectory(factory);
    }

    private static byte[] ReadFixtureWav() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcription", "jfk.wav"));

    private static void AssertEmptyUploadDirectory(TestServerWebAppFactory factory)
    {
        // The slot is the only owner of the uploaded bytes, and its disposal is the only thing that deletes them.
        var directory = Path.Combine(factory.Services.GetRequiredService<INodeDataDirectory>().Root, "tmp", "transcription");
        var leftovers = Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
        AssertEx.Empty(leftovers, $"The engine-owned upload directory must hold no audio after the request ({string.Join(", ", leftovers)}).");
    }

    private static async Task<Guid> CreateSessionAsync(TestServerWebAppFactory factory, HttpClient client)
    {
        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions");
        request.Content = JsonContent.Create(new
        {
            title = "upload-test",
            sourceKind = "File"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response).ConfigureAwait(false);
        return body.GetProperty("session").GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> UploadAsync(TestServerWebAppFactory factory,
        HttpClient client,
        Guid sessionId,
        string fileName,
        byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/file");
        request.Content = form;
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
    }

    /// <summary>
    ///     The real service on the real store, with only the three runtime seams faked: the whisper daemon, its
    ///     supervisor, and the ffmpeg transcoder. Nothing about the upload path itself is substituted.
    /// </summary>
    private static TestServerWebAppFactory FactoryWith(FakeWhisperTranscriber transcriber,
        bool transcoderAvailable = true,
        Action<IServiceCollection>? extraServices = null) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWhisperTranscriber>();
                services.AddSingleton<IWhisperTranscriber>(transcriber);
                services.RemoveAll<IWhisperServerSupervisor>();
                services.AddSingleton<IWhisperServerSupervisor>(new FakeWhisperServerSupervisor());
                services.RemoveAll<IAudioTranscoder>();
                services.AddSingleton<IAudioTranscoder>(new FakeAudioTranscoder
                {
                    IsAvailable = transcoderAvailable
                });
                services.RemoveAll<ITranscriptionRuntimeService>();
                services.AddSingleton<ITranscriptionRuntimeService>(new FakeTranscriptionRuntimeService("ggml-tiny"));
                extraServices?.Invoke(services);
            }
        };
}
