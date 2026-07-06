namespace XE_Local_AI_Engine.Tests.Images;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the generated-image blob store encrypts image bytes at rest (the on-disk blob is not the plaintext PNG) and
///     round-trips them back through the decrypt path, and that the metadata row is persisted alongside.
/// </summary>
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
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
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
            }, CancellationToken.None).ConfigureAwait(false);
        }

        using var keyHolder = new NullNodeSqliteKeyHolder();
        var store = new GeneratedImageStore(scopeFactory,
            new FakeNodeDataDirectory(_rootPath),
            keyHolder,
            TimeProvider.System);

        var info = await store.AddAsync(jobId, imageId, pngBytes, new GeneratedImageMetadata
        {
            Width = 512,
            Height = 512
        }, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(imageId, info.ImageId);
        AssertEx.Equal("image/png", info.MimeType);

        // At-rest: the on-disk blob must NOT be the plaintext PNG (nonce||ciphertext||tag framing, so also longer).
        var onDiskPath = Path.Combine(_rootPath, "generated-images", jobId.ToString("D"), string.Concat(imageId.ToString("D"), ".png"));
        AssertEx.True(File.Exists(onDiskPath), "The encrypted blob must be written to disk.");
        var onDisk = await File.ReadAllBytesAsync(onDiskPath).ConfigureAwait(false);
        AssertEx.True(onDisk.Length > pngBytes.Length, "The encrypted blob carries nonce + tag overhead.");
        AssertEx.False(ContainsSubsequence(onDisk, pngBytes), "The plaintext PNG bytes must not appear in the on-disk blob.");

        // Round-trip: the decrypt path returns the exact original bytes.
        var content = AssertEx.NotNull(await store.OpenReadAsync(imageId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal("image/png", content.MimeType);
        AssertEx.True(content.Bytes.Span.SequenceEqual(pngBytes), "OpenRead must decrypt back to the original PNG bytes.");
    }

    [Test]
    public async Task OpenReadAsync_WhenImageUnknown_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync().ConfigureAwait(false);
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var store = new GeneratedImageStore(provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeNodeDataDirectory(_rootPath),
            keyHolder,
            TimeProvider.System);

        var content = await store.OpenReadAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        AssertEx.Null(content);
    }

    private async Task<ServiceProvider> BuildProviderAsync()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "images.sqlite");

        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

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
