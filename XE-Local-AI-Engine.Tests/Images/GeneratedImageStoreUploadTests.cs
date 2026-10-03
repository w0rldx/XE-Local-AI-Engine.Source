namespace XE_Local_AI_Engine.Tests.Images;

using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Uploaded images over real SQLite and disk: encrypted job-less blobs that read back, job-backed reads unaffected,
///     a contained blob remove, newest-first listing, an upload delete that never touches a job's image, no orphan blob.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GeneratedImageStoreUploadTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddUploadAsync_ThenOpenRead_RoundTripsAJobLessBlobEncryptedAtRest()
    {
        await using var provider = await BuildProviderAsync();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(provider, keyHolder, TimeProvider.System);
        var bytes = Encoding.UTF8.GetBytes("JPEG-PAYLOAD-a-distinctive-uploaded-photo-for-the-at-rest-assertion");

        var info = await images.AddUploadAsync(bytes, new GeneratedImageMetadata
        {
            Width = 640,
            Height = 480,
            MimeType = "image/jpeg"
        }, CancellationToken.None);

        AssertEx.Null(info.JobId);
        var onDiskPath = Path.Combine(_rootPath, "generated-images", "uploads", string.Concat(info.ImageId.ToString("D"), ".jpg"));
        AssertEx.True(File.Exists(onDiskPath), "The upload is written under uploads/ with the extension of its format.");
        var onDisk = await File.ReadAllBytesAsync(onDiskPath);
        AssertEx.False(onDisk.AsSpan().IndexOf(bytes) >= 0, "The plaintext upload must not appear in the on-disk blob.");

        var content = AssertEx.NotNull(await images.OpenReadAsync(info.ImageId, CancellationToken.None));
        AssertEx.Equal("image/jpeg", content.MimeType);
        AssertEx.Equal(expected: 640, content.Width);
        AssertEx.True(content.Bytes.Span.SequenceEqual(bytes), "OpenRead must decrypt the upload with its job-less associated data.");
    }

    [Test]
    public async Task OpenReadAsync_ForAJobBackedImage_StillDecryptsBesideAnUpload()
    {
        await using var provider = await BuildProviderAsync();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(provider, keyHolder, TimeProvider.System);

        var (_, jobImage) = await SeedJobImageAsync(provider, images, "job-produced");
        _ = await AddUploadAsync(images, "uploaded");

        var content = AssertEx.NotNull(await images.OpenReadAsync(jobImage, CancellationToken.None));
        AssertEx.Equal("job-produced", Encoding.UTF8.GetString(content.Bytes.Span));
    }

    [Test]
    public async Task RemoveUploadBlob_DeletesAnInRootBlob_AndLeavesAPathOutsideTheUploadRoot()
    {
        await using var provider = await BuildProviderAsync();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(provider, keyHolder, TimeProvider.System);

        var upload = await AddUploadAsync(images, "doomed upload");
        var inRoot = Path.Combine(_rootPath, "generated-images", "uploads", string.Concat(upload.ImageId.ToString("D"), ".png"));
        var outside = Path.Combine(_rootPath, "outside.png");
        await File.WriteAllTextAsync(outside, "must survive");
        var traversal = Path.Combine(_rootPath, "generated-images", "uploads", "..", "..", "outside.png");

        images.RemoveUploadBlob(upload.ImageId, traversal);
        images.RemoveUploadBlob(upload.ImageId, outside);
        images.RemoveUploadBlob(upload.ImageId, inRoot);

        AssertEx.True(File.Exists(outside), "A path outside the upload root must never be unlinked.");
        AssertEx.False(File.Exists(inRoot), "The upload's own blob must be removed.");
    }

    [Test]
    public async Task ListUploadsAsync_ReturnsOnlyUploads_NewestFirst_PagedByLimitAndOffset_AndCountsThem()
    {
        await using var provider = await BuildProviderAsync();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var clock = new SteppingTimeProvider();
        var images = NewImageStore(provider, keyHolder, clock);

        var oldest = await AddUploadAsync(images, "oldest");
        _ = await SeedJobImageAsync(provider, images, "job image, never listed");
        var middle = await AddUploadAsync(images, "middle");
        var newest = await AddUploadAsync(images, "newest");

        await using var scope = provider.CreateAsyncScope();
        var rows = scope.ServiceProvider.GetRequiredService<IGeneratedImageRowStore>();

        var all = await rows.ListUploadsAsync(limit: 10, offset: 0, CancellationToken.None);
        AssertEx.True(all.Select(static row => row.ImageId).SequenceEqual([newest.ImageId, middle.ImageId, oldest.ImageId]),
            "Every upload is listed, newest first, and the job image is not.");
        AssertEx.True(all.All(static row => row.JobId is null), "Only job-less rows are uploads.");

        var page = await rows.ListUploadsAsync(limit: 2, offset: 0, CancellationToken.None);
        AssertEx.True(page.Select(static row => row.ImageId).SequenceEqual([newest.ImageId, middle.ImageId]), "The limit keeps the newest rows.");

        var second = await rows.ListUploadsAsync(limit: 2, offset: 2, CancellationToken.None);
        AssertEx.True(second.Select(static row => row.ImageId).SequenceEqual([oldest.ImageId]), "The offset skips the first page.");

        var negative = await rows.ListUploadsAsync(limit: 1, offset: -5, CancellationToken.None);
        AssertEx.True(negative.Select(static row => row.ImageId).SequenceEqual([newest.ImageId]), "A negative offset is floored at 0.");

        AssertEx.Equal(expected: 3, await rows.CountUploadsAsync(CancellationToken.None), "The count ignores paging and job images.");
    }

    [Test]
    public async Task DeleteUploadAsync_RemovesAnUploadRow_AndRefusesAJobBackedRow()
    {
        await using var provider = await BuildProviderAsync();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(provider, keyHolder, TimeProvider.System);

        var upload = await AddUploadAsync(images, "upload");
        var (_, jobImage) = await SeedJobImageAsync(provider, images, "job image");

        await using var scope = provider.CreateAsyncScope();
        var rows = scope.ServiceProvider.GetRequiredService<IGeneratedImageRowStore>();

        AssertEx.Null(await rows.DeleteUploadAsync(jobImage, CancellationToken.None), "A job-backed image is not an upload.");
        AssertEx.NotNull(await rows.FindAsync(jobImage, CancellationToken.None), "The job-backed row must survive the refused delete.");

        var storagePath = AssertEx.NotNull(await rows.DeleteUploadAsync(upload.ImageId, CancellationToken.None));
        AssertEx.True(storagePath.EndsWith(string.Concat(upload.ImageId.ToString("D"), ".png"), StringComparison.Ordinal));
        AssertEx.Null(await rows.FindAsync(upload.ImageId, CancellationToken.None));
        AssertEx.Equal(expected: 1L, await CountImageRowsAsync(provider), "Only the upload row was deleted.");
        AssertEx.Null(await rows.DeleteUploadAsync(upload.ImageId, CancellationToken.None), "A second delete finds nothing.");
    }

    [Test]
    public async Task AddUploadAsync_WhenTheRowInsertThrows_RemovesTheWrittenBlob_AndRethrows()
    {
        var rows = Substitute.For<IGeneratedImageRowStore>();
        rows.InsertAsync(Arg.Any<GeneratedImageRow>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("row insert failed"));
        await using var provider = BuildProviderOver(rows);
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(provider, keyHolder, TimeProvider.System);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => AddUploadAsync(images, "never persisted"));

        await rows.ReceivedWithAnyArgs(1).InsertAsync(default!, default!, default);
        AssertEx.Empty(Directory.GetFiles(UploadsDirectory()), "A blob whose row never landed must not stay on disk.");
    }

    [Test]
    public async Task AddUploadAsync_WhenCancelledAtInsert_RemovesTheWrittenBlob_AndRethrows()
    {
        using var cancellation = new CancellationTokenSource();
        var rows = Substitute.For<IGeneratedImageRowStore>();
        rows.InsertAsync(Arg.Any<GeneratedImageRow>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                // The blob is already written when the insert sees the cancellation.
                AssertEx.True(File.Exists(call.ArgAt<string>(1)), "The blob is on disk before the row insert runs.");
                await cancellation.CancelAsync();
                call.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
            });
        await using var provider = BuildProviderOver(rows);
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(provider, keyHolder, TimeProvider.System);

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            images.AddUploadAsync(Encoding.UTF8.GetBytes("cancelled upload"), new GeneratedImageMetadata
            {
                Width = 8,
                Height = 8
            }, cancellation.Token));

        await rows.ReceivedWithAnyArgs(1).InsertAsync(default!, default!, default);
        AssertEx.Empty(Directory.GetFiles(UploadsDirectory()), "A cancelled upload must not leave its blob behind.");
    }

    [Test]
    public async Task AddUploadAsync_OnSuccess_KeepsTheBlobAndTheRow()
    {
        await using var provider = await BuildProviderAsync();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(provider, keyHolder, TimeProvider.System);

        var upload = await AddUploadAsync(images, "kept");

        AssertEx.True(File.Exists(Path.Combine(UploadsDirectory(), string.Concat(upload.ImageId.ToString("D"), ".png"))), "The blob stays on success.");
        await using var scope = provider.CreateAsyncScope();
        AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<IGeneratedImageRowStore>().FindAsync(upload.ImageId, CancellationToken.None));
    }

    private static Task<GeneratedImageInfo> AddUploadAsync(GeneratedImageStore images, string payload)
    {
        return images.AddUploadAsync(Encoding.UTF8.GetBytes(payload), new GeneratedImageMetadata
        {
            Width = 64,
            Height = 64
        }, CancellationToken.None);
    }

    private static async Task<(Guid JobId, Guid ImageId)> SeedJobImageAsync(ServiceProvider provider, GeneratedImageStore images, string payload)
    {
        var jobId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var jobStore = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());
            await jobStore.CreateQueuedAsync(new ImageJobCreate
            {
                Id = jobId,
                ModelName = "leejet/stable-diffusion-1.5-gguf",
                Prompt = "irrelevant",
                Seed = -1,
                Width = 512,
                Height = 512,
                Steps = 20,
                Sampler = "euler_a",
                CfgScale = 7.0,
                CreatedAtUtc = 100
            }, CancellationToken.None);
        }

        _ = await images.AddAsync(jobId, imageId, Encoding.UTF8.GetBytes(payload), new GeneratedImageMetadata
        {
            Width = 512,
            Height = 512
        }, CancellationToken.None);
        return (jobId, imageId);
    }

    private static async Task<long> CountImageRowsAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await using var command = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM generated_images;";
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync(CancellationToken.None);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    private string UploadsDirectory()
    {
        return Path.Combine(_rootPath, "generated-images", "uploads");
    }

    private static ServiceProvider BuildProviderOver(IGeneratedImageRowStore rows)
    {
        var services = new ServiceCollection();
        services.AddSingleton(rows);
        return services.BuildServiceProvider();
    }

    private GeneratedImageStore NewImageStore(ServiceProvider provider, NullNodeSqliteKeyHolder keyHolder, TimeProvider timeProvider)
    {
        return new GeneratedImageStore(provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeNodeDataDirectory(_rootPath),
            keyHolder,
            timeProvider,
            NullLogger<GeneratedImageStore>.Instance);
    }

    private async Task<ServiceProvider> BuildProviderAsync()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "uploads.sqlite");

        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<IGeneratedImageRowStore, GeneratedImageRowStore>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();
        return provider;
    }

    /// <summary>Advances one second per read, so rows written in sequence get strictly increasing timestamps.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddYears(56);

        public override DateTimeOffset GetUtcNow()
        {
            _now = _now.AddSeconds(1);
            return _now;
        }
    }
}
