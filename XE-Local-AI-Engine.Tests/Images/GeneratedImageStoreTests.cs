namespace XE_Local_AI_Engine.Tests.Images;

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the generated-image blob store encrypts image bytes at rest (the on-disk blob is not the plaintext PNG) and
///     round-trips them back through the decrypt path, that the metadata row is persisted alongside, and that a
///     job-scoped delete takes both rows and the blob with it while leaving every other job untouched.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GeneratedImageStoreTests : IDisposable
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
    public async Task AddAsync_ThenOpenRead_RoundTripsBytesEncryptedAtRest()
    {
        await using var provider = await BuildProviderAsync();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var jobId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var pngBytes = Encoding.UTF8.GetBytes("PNG-PAYLOAD-an-utterly-distinctive-image-blob-for-encryption-assertion");

        // A parent job row must exist (generated_images carries a cascade FK to image_jobs).
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var jobStore = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());
            await jobStore.CreateQueuedAsync(new ImageJobCreate
            {
                Id = jobId,
                ModelName = "leejet/stable-diffusion-1.5-gguf",
                Prompt = "irrelevant prompt",
                Seed = -1,
                Width = 512,
                Height = 512,
                Steps = 20,
                Sampler = "euler_a",
                CfgScale = 7.0,
                CreatedAtUtc = 100
            }, CancellationToken.None);
        }

        using var keyHolder = new NullNodeSqliteKeyHolder();
        var store = new GeneratedImageStore(scopeFactory,
            new FakeNodeDataDirectory(_rootPath),
            keyHolder,
            TimeProvider.System,
            NullLogger<GeneratedImageStore>.Instance);

        var info = await store.AddAsync(jobId, imageId, pngBytes, new GeneratedImageMetadata
        {
            Width = 512,
            Height = 512
        }, CancellationToken.None);
        AssertEx.Equal(imageId, info.ImageId);
        AssertEx.Equal("image/png", info.MimeType);

        // At-rest: the on-disk blob must NOT be the plaintext PNG (nonce||ciphertext||tag framing, so also longer).
        var onDiskPath = Path.Combine(_rootPath, "generated-images", jobId.ToString("D"), string.Concat(imageId.ToString("D"), ".png"));
        AssertEx.True(File.Exists(onDiskPath), "The encrypted blob must be written to disk.");
        var onDisk = await File.ReadAllBytesAsync(onDiskPath);
        AssertEx.True(onDisk.Length > pngBytes.Length, "The encrypted blob carries nonce + tag overhead.");
        AssertEx.False(ContainsSubsequence(onDisk, pngBytes), "The plaintext PNG bytes must not appear in the on-disk blob.");

        // Round-trip: the decrypt path returns the exact original bytes.
        var content = AssertEx.NotNull(await store.OpenReadAsync(imageId, CancellationToken.None));
        AssertEx.Equal("image/png", content.MimeType);
        AssertEx.True(content.Bytes.Span.SequenceEqual(pngBytes), "OpenRead must decrypt back to the original PNG bytes.");
    }

    [Test]
    public async Task OpenReadAsync_WhenImageUnknown_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var store = new GeneratedImageStore(provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeNodeDataDirectory(_rootPath),
            keyHolder,
            TimeProvider.System,
            NullLogger<GeneratedImageStore>.Instance);

        var content = await store.OpenReadAsync(Guid.NewGuid(), CancellationToken.None);
        AssertEx.Null(content);
    }

    /// <summary>
    ///     The whole job-scoped delete over real SQLite and the real file system: the job row, its <c>generated_images</c>
    ///     row and its encrypted blob all go, and a second job that was never named keeps all three. The totals are read
    ///     UNFILTERED, so an over-broad delete is visible: a query scoped to the deleted job could not see one. The
    ///     storage paths the delete returns are what the blob teardown needs, and no cascade can produce them.
    /// </summary>
    [Test]
    public async Task DeleteAsync_RemovesTheJobRowsAndBlob_LeavingAnotherJobIntact()
    {
        await using var provider = await BuildProviderAsync();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(scopeFactory, keyHolder);

        var (doomedJob, doomedImage) = await SeedJobWithImageAsync(scopeFactory, images, "doomed prompt");
        var (survivingJob, survivingImage) = await SeedJobWithImageAsync(scopeFactory, images, "surviving prompt");

        var doomedBlob = BlobPath(doomedJob, doomedImage);
        var survivingBlob = BlobPath(survivingJob, survivingImage);
        AssertEx.True(File.Exists(doomedBlob), "The blob under test must exist before the delete.");

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var jobStore = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());
            var storagePaths = AssertEx.NotNull(await jobStore.DeleteAsync(doomedJob, CancellationToken.None));
            AssertEx.Equal(expected: 1, storagePaths.Count, "The delete reports exactly the one blob it unreferenced.");
            AssertEx.Equal(doomedBlob, storagePaths[0]);
            images.RemoveJobBlobs(doomedJob, storagePaths);
        }

        AssertEx.Equal(expected: 1L, await CountJobRowsAsync(scopeFactory), "Exactly one job row may survive the delete.");
        AssertEx.Equal(expected: 1L, await CountImageRowsAsync(scopeFactory), "Exactly one image row may survive the delete.");

        AssertEx.False(File.Exists(doomedBlob), "The deleted job's encrypted blob must be gone from disk.");
        AssertEx.False(Directory.Exists(Path.GetDirectoryName(doomedBlob)!), "The deleted job's now-empty directory must be gone.");
        AssertEx.True(File.Exists(survivingBlob), "The other job's blob must survive.");
        AssertEx.NotNull(await images.OpenReadAsync(survivingImage, CancellationToken.None), "The other job's image must still read back.");
        AssertEx.Null(await images.OpenReadAsync(doomedImage, CancellationToken.None), "The deleted image must no longer resolve.");
    }

    [Test]
    public async Task DeleteAsync_WhenJobUnknown_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync();
        await using var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var jobStore = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());

        AssertEx.Null(await jobStore.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>
    ///     The rows are already gone by the time the blobs are unlinked, so a file that cannot be removed must be
    ///     swallowed — anything else would surface a failure for a delete that has in fact happened. A directory
    ///     standing where a blob path should be is the cheapest real unlink failure on every OS.
    /// </summary>
    [Test]
    public async Task RemoveJobBlobs_WhenAPathCannotBeUnlinked_SwallowsItAndRemovesTheRest()
    {
        await using var provider = await BuildProviderAsync();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(scopeFactory, keyHolder);

        var (jobId, imageId) = await SeedJobWithImageAsync(scopeFactory, images, "blob that unlinks");
        var deletable = BlobPath(jobId, imageId);
        var undeletable = Path.Combine(Path.GetDirectoryName(deletable)!, "not-a-file.png");
        Directory.CreateDirectory(undeletable);
        await File.WriteAllTextAsync(Path.Combine(undeletable, "occupant.txt"), "keeps the directory non-empty");

        images.RemoveJobBlobs(jobId, [undeletable, deletable]);

        AssertEx.False(File.Exists(deletable), "A blob after the failing one must still be unlinked.");
        AssertEx.True(Directory.Exists(undeletable), "The path that could not be unlinked is left in place, not forced.");
    }

    /// <summary>
    ///     The deletion boundary enforces its own invariant. A <c>storage_path</c> that escapes the image blob root —
    ///     whether by traversal or as a bare absolute path — must never be unlinked, however it got into the row
    ///     (migration, hand edit, a future writer that stops minting the path). The delete still succeeds and a
    ///     control job's in-root blob still goes, so the guard cannot be mistaken for "delete stopped working".
    /// </summary>
    [Test]
    public async Task DeleteAsync_WhenAStoragePathEscapesTheBlobRoot_LeavesThatFileAndStillRemovesAnInRootBlob()
    {
        await using var provider = await BuildProviderAsync();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var images = NewImageStore(scopeFactory, keyHolder);

        var (controlJob, controlImage) = await SeedJobWithImageAsync(scopeFactory, images, "in-root control");
        var (traversalJob, traversalImage) = await SeedJobWithImageAsync(scopeFactory, images, "row pointing out by traversal");
        var (absoluteJob, absoluteImage) = await SeedJobWithImageAsync(scopeFactory, images, "row pointing out absolutely");

        // Two files OUTSIDE the blob root (but inside the test's own temp root, so Dispose still cleans them up),
        // reached the two ways a rogue row could reach them.
        var outsideByTraversal = Path.Combine(_rootPath, "outside-by-traversal.png");
        var outsideByAbsolutePath = Path.Combine(_rootPath, "outside-by-absolute-path.png");
        await File.WriteAllTextAsync(outsideByTraversal, "must survive");
        await File.WriteAllTextAsync(outsideByAbsolutePath, "must survive");

        // `{blobRoot}/{jobId}/../../outside-by-traversal.png` resolves to the file above — the classic escape, left
        // un-normalized in the column exactly as a bad migration would write it.
        await RewriteStoragePathAsync(scopeFactory,
            traversalImage,
            Path.Combine(_rootPath, "generated-images", traversalJob.ToString("D"), "..", "..", "outside-by-traversal.png"));
        await RewriteStoragePathAsync(scopeFactory, absoluteImage, outsideByAbsolutePath);

        foreach (var jobId in new[] { traversalJob, absoluteJob, controlJob })
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobStore = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());
            var storagePaths = AssertEx.NotNull(await jobStore.DeleteAsync(jobId, CancellationToken.None),
                "The rows go whatever the recorded path says — the guard is on the unlink, not on the delete.");
            images.RemoveJobBlobs(jobId, storagePaths);
        }

        AssertEx.True(File.Exists(outsideByTraversal), "A traversal path must not reach a file outside the image blob root.");
        AssertEx.True(File.Exists(outsideByAbsolutePath), "An absolute path outside the image blob root must not be unlinked.");
        AssertEx.False(File.Exists(BlobPath(controlJob, controlImage)), "The control job's in-root blob must still be removed.");
        AssertEx.Equal(expected: 0L, await CountJobRowsAsync(scopeFactory), "Every job row was deleted; only the unlink is guarded.");
        AssertEx.Equal(expected: 0L, await CountImageRowsAsync(scopeFactory), "Every image row was deleted; only the unlink is guarded.");
    }

    // Rewrites one image's recorded path to something the store would never mint, standing in for a legacy or
    // hand-edited row. The value is a parameter, never interpolated into the SQL.
    private static async Task RewriteStoragePathAsync(IServiceScopeFactory scopeFactory, Guid imageId, string storagePath)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "UPDATE generated_images SET storage_path = $storage_path WHERE image_id = $image_id;";

        var pathParameter = command.CreateParameter();
        pathParameter.ParameterName = "$storage_path";
        pathParameter.Value = storagePath;
        _ = command.Parameters.Add(pathParameter);

        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$image_id";
        // The raw Guid, exactly as AddAsync binds it, so the provider formats both identically — SQLite compares
        // TEXT case-sensitively, and the affected-row assertion below is what proves the match.
        idParameter.Value = imageId;
        _ = command.Parameters.Add(idParameter);

        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync(CancellationToken.None);
        }

        AssertEx.Equal(expected: 1, await command.ExecuteNonQueryAsync(CancellationToken.None), "The tampered row must exist.");
    }

    private GeneratedImageStore NewImageStore(IServiceScopeFactory scopeFactory, NullNodeSqliteKeyHolder keyHolder)
    {
        return new GeneratedImageStore(scopeFactory,
            new FakeNodeDataDirectory(_rootPath),
            keyHolder,
            TimeProvider.System,
            NullLogger<GeneratedImageStore>.Instance);
    }

    private string BlobPath(Guid jobId, Guid imageId)
    {
        return Path.Combine(_rootPath, "generated-images", jobId.ToString("D"), string.Concat(imageId.ToString("D"), ".png"));
    }

    private static async Task<(Guid JobId, Guid ImageId)> SeedJobWithImageAsync(IServiceScopeFactory scopeFactory,
        IGeneratedImageStore images,
        string prompt)
    {
        var jobId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var jobStore = new ImageJobStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>());
            await jobStore.CreateQueuedAsync(new ImageJobCreate
            {
                Id = jobId,
                ModelName = "leejet/stable-diffusion-1.5-gguf",
                Prompt = prompt,
                Seed = -1,
                Width = 512,
                Height = 512,
                Steps = 20,
                Sampler = "euler_a",
                CfgScale = 7.0,
                CreatedAtUtc = 100
            }, CancellationToken.None);
        }

        _ = await images.AddAsync(jobId, imageId, Encoding.UTF8.GetBytes(prompt), new GeneratedImageMetadata
        {
            Width = 512,
            Height = 512
        }, CancellationToken.None);

        return (jobId, imageId);
    }

    // Unfiltered row counts straight over the connection: the assertions must be about the whole table, not about the
    // rows a job-scoped query would return. Each SQL text is a literal at its own call site (a shared string parameter
    // is what CA2100 objects to), so only the open + convert plumbing is shared.
    private static async Task<long> CountJobRowsAsync(IServiceScopeFactory scopeFactory)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var command = NewCommand(scope);
        command.CommandText = "SELECT COUNT(*) FROM image_jobs;";
        return await ExecuteCountAsync(command);
    }

    private static async Task<long> CountImageRowsAsync(IServiceScopeFactory scopeFactory)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var command = NewCommand(scope);
        command.CommandText = "SELECT COUNT(*) FROM generated_images;";
        return await ExecuteCountAsync(command);
    }

    private static DbCommand NewCommand(AsyncServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection().CreateCommand();
    }

    private static async Task<long> ExecuteCountAsync(DbCommand command)
    {
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync(CancellationToken.None);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    private async Task<ServiceProvider> BuildProviderAsync()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "images.sqlite");

        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        // The node enforces foreign keys, so this fixture must too — it used to pin `Foreign Keys=False` on the belief
        // that the node ran without enforcement, which made it the one fixture here testing a database production never
        // has. The delete test below stays meaningful under the real posture because the cascade cannot produce the
        // storage paths `DeleteAsync` returns, and those paths are the whole point of the explicit ordered delete.
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath};Foreign Keys=True"));

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        return provider;
    }

    private static bool ContainsSubsequence(byte[] source, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var sourceIndex = 0; sourceIndex <= source.Length - needle.Length; sourceIndex++)
        {
            var matched = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (source[sourceIndex + needleIndex] == needle[needleIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
