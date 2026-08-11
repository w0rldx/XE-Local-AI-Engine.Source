namespace XE_Local_AI_Engine.Tests.Endpoints.LocalChat;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Tests.Testing;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     Contract tests for the conversation file-upload endpoint: the size cap and extension allowlist reject before any
///     persistence, and a traversal-laden client file name is reduced to a safe leaf on a successful upload (the
///     server-generated storage path is never influenced by the client string).
/// </summary>
public sealed class ConversationUploadEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Upload_WhenOversize_Rejects()
    {
        // Drop the cap to 1 MB for this host so the test ships a small (2 MB) payload rather than a 25 MB one.
        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services => services.Configure<SecurityOptions>(options => options.MaxUploadFileSizeMb = 1)
        };
        using var client = factory.CreateClient();

        var oversized = new byte[2 * 1024 * 1024];
        using var response = await UploadAsync(factory, client, Guid.NewGuid(), "big.txt", oversized, "text/plain").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Upload_WhenUnsupportedExtension_Rejects()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        // A genuinely unsupported type: not a text/pdf/docx the extractor handles, and not an accepted image. It is
        // rejected at the admission gate before any persistence, so no conversation seed is needed.
        var bytes = new byte[]
        {
            0x4D,
            0x5A,
            0x00,
            0x00
        };
        using var response = await UploadAsync(factory, client, Guid.NewGuid(), "installer.exe", bytes, "application/octet-stream").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Upload_WhenImage_AcceptsWithImageStatus()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var conversationId = await CreateConversationAsync(factory, client).ConfigureAwait(false);

        // Minimal PNG signature — image bytes are stored as-is (no extraction), so content need not be a full image.
        var pngSignature = new byte[]
        {
            0x89,
            0x50,
            0x4E,
            0x47,
            0x0D,
            0x0A,
            0x1A,
            0x0A
        };
        using var response = await UploadAsync(factory, client, conversationId, "photo.png", pngSignature, "image/png").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var uploaded = AssertEx.NotNull(await JsonSerializer.DeserializeAsync<UploadedFileWireDto>(stream, JsonOptions).ConfigureAwait(false));

        // An image is admitted for vision input and persisted with the Image status (no text extraction).
        AssertEx.Equal("photo.png", uploaded.OriginalFileName);
        AssertEx.Equal("Image", uploaded.ExtractionStatus);
    }

    [Test]
    public async Task Upload_WhenTraversalFilename_SanitizesToLeaf()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var conversationId = await CreateConversationAsync(factory, client).ConfigureAwait(false);

        var bytes = Encoding.UTF8.GetBytes("ground-truth content for the upload");
        using var response = await UploadAsync(factory, client, conversationId, "../../etc/secret.txt", bytes, "text/plain").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var uploaded = AssertEx.NotNull(await JsonSerializer.DeserializeAsync<UploadedFileWireDto>(stream, JsonOptions).ConfigureAwait(false));

        // The traversal segments are stripped to a leaf; the directory part never reaches storage.
        AssertEx.Equal("secret.txt", uploaded.OriginalFileName);
        AssertEx.Equal("Extracted", uploaded.ExtractionStatus);
    }

    private static async Task<Guid> CreateConversationAsync(TestingWebAppFactory factory, HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/chat/conversations")
        {
            Content = JsonContent.Create(new
            {
                title = "uploads-test"
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        return document.RootElement.GetProperty("conversationId").GetGuid();
    }

    private static async Task<HttpResponseMessage> UploadAsync(TestingWebAppFactory factory,
        HttpClient client,
        Guid conversationId,
        string fileName,
        byte[] bytes,
        string contentType)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/local/v1/chat/conversations/{conversationId}/uploads")
        {
            Content = form
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    // Local wire shapes — the endpoint DTOs are internal to the Client project's V1 namespace, so the test mirrors the
    // JSON contract rather than referencing those types.
    private sealed record UploadedFileWireDto
    {
        public string OriginalFileName { get; init; } = string.Empty;

        public string ExtractionStatus { get; init; } = string.Empty;
    }
}
