namespace XE_Local_AI_Engine.Tests.Endpoints.Images;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Tests.Testing;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     The upload routes over the real host: PNG and JPEG are accepted and listed (paged, with a total), retrieve serves
///     the stored MIME type, unacceptable bodies are refused with 400, and delete answers 204 then 404.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class UploadedImageEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task UploadRoutes_RequireOperator()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        foreach (var (method, route) in new[]
                 {
                     (HttpMethod.Get, $"{ApiPrefix}/images/uploads"),
                     (HttpMethod.Post, $"{ApiPrefix}/images/uploads"),
                     (HttpMethod.Delete, $"{ApiPrefix}/images/uploads/{Guid.NewGuid()}")
                 })
        {
            using var request = new HttpRequestMessage(method, route);
            using var response = await client.SendAsync(request);
            AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
        }
    }

    [Test]
    public async Task Upload_Png_IsStoredListedAndRetrievable()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        var png = Png(width: 640, height: 480);

        using var response = await UploadAsync(factory, client, png);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var uploaded = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal("image/png", uploaded.GetProperty("mimeType").GetString());
        AssertEx.Equal(expected: 640, uploaded.GetProperty("width").GetInt32());
        AssertEx.Equal(expected: 480, uploaded.GetProperty("height").GetInt32());
        var imageId = uploaded.GetProperty("imageId").GetGuid();

        var listed = await ListAsync(factory, client);
        AssertEx.Equal(expected: 1, listed.GetProperty("items").GetArrayLength());
        AssertEx.Equal(imageId, listed.GetProperty("items")[0].GetProperty("imageId").GetGuid());

        using var retrieve = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/images/{imageId}");
        using var retrieved = await client.SendAsync(retrieve);
        AssertEx.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        AssertEx.True((await retrieved.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(png), "Retrieve returns the original bytes.");
    }

    [Test]
    public async Task Upload_RgbJpeg_IsAccepted_AndRetrieveServesTheJpegMimeType()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await UploadAsync(factory, client, Jpeg(width: 800, height: 600, components: 3));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var uploaded = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal("image/jpeg", uploaded.GetProperty("mimeType").GetString());

        using var retrieve = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/images/{uploaded.GetProperty("imageId").GetGuid()}");
        using var retrieved = await client.SendAsync(retrieve);
        AssertEx.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        AssertEx.Equal("image/jpeg", retrieved.Content.Headers.ContentType?.MediaType);
    }

    [Test]
    public async Task Upload_UnacceptableImages_AreRefusedWith400AndNothingIsStored()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        (byte[] Bytes, string Message)[] cases =
        [
            (Jpeg(width: 800, height: 600, components: 4), "CMYK JPEG images are not supported."),
            (Png(width: 2049, height: 512), "Images larger than 2048 pixels on either side are not supported."),
            (Jpeg(width: 512, height: 4096, components: 3), "Images larger than 2048 pixels on either side are not supported."),
            ("GIF89a not an accepted image"u8.ToArray(), "Only PNG and JPEG images can be uploaded."),
            (Png(width: 640, height: 480)[..12], "The image header could not be read.")
        ];

        foreach (var (bytes, message) in cases)
        {
            using var response = await UploadAsync(factory, client, bytes);
            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, message);
            AssertEx.Contains(await response.Content.ReadAsStringAsync(), message);
        }

        AssertEx.Equal(expected: 0, (await ListAsync(factory, client)).GetProperty("items").GetArrayLength());
    }

    [Test]
    public async Task Upload_WithTwoFileParts_Returns400()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var form = new MultipartFormDataContent();
        using var first = new ByteArrayContent(Png(width: 64, height: 64));
        using var second = new ByteArrayContent(Png(width: 64, height: 64));
        form.Add(first, "file", "a.png");
        form.Add(second, "extra", "b.png");
        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/uploads");
        request.Content = form;
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "Exactly one file is accepted.");
        AssertEx.Equal(expected: 0, (await ListAsync(factory, client)).GetProperty("items").GetArrayLength());
    }

    [Test]
    public async Task Upload_PastTheSizeCap_Returns400()
    {
        // A 1 MB cap for this host keeps the body small; the cap is enforced while the section is copied.
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services => services.Configure<SecurityOptions>(options => options.MaxUploadFileSizeMb = 1)
        };
        using var client = factory.CreateClient();
        byte[] oversized = [.. Png(width: 64, height: 64), .. new byte[(1024 * 1024) + 1]];

        using var response = await UploadAsync(factory, client, oversized);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "The image is larger than the upload size limit.");
        AssertEx.Equal(expected: 0, (await ListAsync(factory, client)).GetProperty("items").GetArrayLength());
    }

    [Test]
    public async Task ListUploads_PagesByLimitAndOffset_ReportsTheTotal_AndRefusesANegativeOffset()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        var uploaded = new HashSet<Guid>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await UploadAsync(factory, client, Png(width: 64, height: 64));
            uploaded.Add((await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("imageId").GetGuid());
        }

        var first = await ListAsync(factory, client, "?limit=2&offset=0");
        var second = await ListAsync(factory, client, "?limit=2&offset=2");

        AssertEx.Equal(expected: 2, first.GetProperty("items").GetArrayLength());
        AssertEx.Equal(expected: 1, second.GetProperty("items").GetArrayLength(), "The offset skips the first page.");
        AssertEx.Equal(expected: 3, first.GetProperty("totalCount").GetInt32(), "The total ignores paging.");
        AssertEx.Equal(expected: 3, second.GetProperty("totalCount").GetInt32());
        var paged = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
                         .Select(static item => item.GetProperty("imageId").GetGuid());
        AssertEx.True(uploaded.SetEquals(paged), "The two pages together hold every upload exactly once.");

        using var negative = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/images/uploads?offset=-1");
        using var refused = await client.SendAsync(negative);
        AssertEx.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        AssertEx.Contains(await refused.Content.ReadAsStringAsync(), "The offset cannot be negative.");
    }

    [Test]
    public async Task DeleteUpload_Returns204ThenTheImageIsGone_AndAnUnknownIdIs404()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var uploadResponse = await UploadAsync(factory, client, Png(width: 64, height: 64));
        var imageId = (await uploadResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("imageId").GetGuid();

        using var delete = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/images/uploads/{imageId}");
        using var deleted = await client.SendAsync(delete);
        AssertEx.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var retrieve = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/images/{imageId}");
        using var retrieved = await client.SendAsync(retrieve);
        AssertEx.Equal(HttpStatusCode.NotFound, retrieved.StatusCode, "A deleted upload no longer resolves.");
        AssertEx.Equal(expected: 0, (await ListAsync(factory, client)).GetProperty("items").GetArrayLength());

        using var again = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/images/uploads/{imageId}");
        using var againResponse = await client.SendAsync(again);
        AssertEx.Equal(HttpStatusCode.NotFound, againResponse.StatusCode);
    }

    private static async Task<HttpResponseMessage> UploadAsync(TestServerWebAppFactory factory, HttpClient client, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        // A misleading declared type: the server decides the format from the bytes alone.
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "photo.bin");
        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/uploads");
        request.Content = form;
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ListAsync(TestServerWebAppFactory factory, HttpClient client, string query = "")
    {
        using var request = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/images/uploads{query}");
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);
        return request;
    }

    // Signature + IHDR header carrying the dimensions; the reader needs nothing past the IHDR size fields.
    private static byte[] Png(int width, int height)
    {
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            0x08, 0x02, 0x00, 0x00, 0x00
        ];
    }

    // SOI, a baseline SOF0 frame with the given component count, a scan and EOI.
    private static byte[] Jpeg(int width, int height, byte components)
    {
        var frameLength = 8 + (3 * components);
        var frame = new List<byte>
        {
            0xFF, 0xC0, (byte)(frameLength >> 8), (byte)frameLength, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, components
        };
        for (var component = 1; component <= components; component++)
        {
            frame.AddRange([(byte)component, 0x11, 0x00]);
        }

        return [0xFF, 0xD8, .. frame, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];
    }
}
