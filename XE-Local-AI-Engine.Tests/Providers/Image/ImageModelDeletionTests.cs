namespace XE_Local_AI_Engine.Tests.Providers.Image;

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins that a delete which could not remove the weights does not report success.
///     <para>
///         The failure mode this guards against is the worst outcome available: the registry entry is dropped while the
///         multi-gigabyte weights stay on disk, so the model disappears from the UI with no remaining way to retry the
///         delete or reclaim the space. The realistic trigger is the running <c>sd-server</c> still holding the file it
///         is serving (a sharing violation on Windows).
///     </para>
/// </summary>
public sealed class ImageModelDeletionTests
{
    private const string ModelName = "sd-1.5";

    [Test]
    public async Task DeleteModel_WhenAWeightFileIsLocked_KeepsTheRegistryEntryAndReportsAConflict()
    {
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) => new HttpResponseMessage());
        using var http = new HttpClient(handler, disposeHandler: false);
        var store = Store(http, models.Path, registry);

        var weightsPath = await SeedInstalledModelAsync(models.Path, registry).ConfigureAwait(false);
        var modelDirectory = Path.GetDirectoryName(weightsPath)!;

        using var block = BlockDeletion(weightsPath, modelDirectory);

        _ = await AssertEx.ThrowsAsync<ImageModelInUseException>(() => store.DeleteModelAsync(ModelName, CancellationToken.None))
                          .ConfigureAwait(false);

        var entry = await registry.FindAsync(ModelName, CancellationToken.None).ConfigureAwait(false);
        AssertEx.NotNull(entry);
        AssertEx.True(File.Exists(weightsPath), "The weights are still on disk, so the model must still be registered.");
    }

    /// <summary>
    ///     Makes the seeded weights genuinely undeletable, by whichever mechanism actually works on this platform.
    /// </summary>
    /// <remarks>
    ///     An exclusive <see cref="FileStream" /> is the Windows story and ONLY the Windows story: POSIX unlink detaches
    ///     the directory entry regardless of open handles, so on Linux that delete would simply succeed and the test
    ///     would assert nothing while appearing to pass. Removing write permission on the parent directory is what
    ///     blocks unlink there. Both branches genuinely block; neither is a silent skip.
    /// </remarks>
    private static IDisposable BlockDeletion(string weightsPath, string modelDirectory)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new ReadOnlyDirectory(modelDirectory);
        }

        return new FileStream(weightsPath, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    /// <summary>Strips write permission from a directory for the scope's lifetime, so unlink inside it fails on Unix.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private sealed class ReadOnlyDirectory : IDisposable
    {
        private readonly string _path;
        private readonly UnixFileMode _original;

        public ReadOnlyDirectory(string path)
        {
            _path = path;
            _original = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            File.SetUnixFileMode(_path, _original);
        }
    }

    [Test]
    public async Task DeleteModel_WhenTheWeightsAreRemovable_RemovesBothTheFileAndTheRegistryEntry()
    {
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) => new HttpResponseMessage());
        using var http = new HttpClient(handler, disposeHandler: false);
        var store = Store(http, models.Path, registry);

        var weightsPath = await SeedInstalledModelAsync(models.Path, registry).ConfigureAwait(false);

        await store.DeleteModelAsync(ModelName, CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(File.Exists(weightsPath), "A successful delete must remove the weights.");
        AssertEx.Null(await registry.FindAsync(ModelName, CancellationToken.None).ConfigureAwait(false));
    }

    // Deleting something that was never installed is not an error — the caller's goal (its absence) already holds.
    [Test]
    public async Task DeleteModel_WhenTheModelIsNotInstalled_Succeeds()
    {
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) => new HttpResponseMessage());
        using var http = new HttpClient(handler, disposeHandler: false);

        await Store(http, models.Path, registry).DeleteModelAsync("never-installed", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(await registry.FindAsync("never-installed", CancellationToken.None).ConfigureAwait(false));
    }

    private static async Task<string> SeedInstalledModelAsync(string modelsDirectory, ImageModelRegistry registry)
    {
        var modelDirectory = Path.Combine(modelsDirectory, HuggingFaceImageModelStore.SafeModelDirectorySegment(ModelName));
        _ = Directory.CreateDirectory(modelDirectory);
        var weightsPath = Path.Combine(modelDirectory, "weights.safetensors");
        await File.WriteAllTextAsync(weightsPath, "weights").ConfigureAwait(false);

        await registry.UpsertAsync(new ImageModelRegistryEntry
        {
            ModelName = ModelName,
            RepoId = "second-state/stable-diffusion-v1-5-GGUF",
            Family = ImageModelFamily.Sd15,
            Kind = ImageModelKind.Txt2Img,
            Parts =
            [
                new ImageModelPart
                {
                    Role = ImageModelPartRole.Diffusion,
                    FileName = "weights.safetensors",
                    LocalPath = weightsPath,
                    SizeBytes = 7
                }
            ],
            SizeBytes = 7,
            SourceRevision = "main",
            DownloadedAtUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None).ConfigureAwait(false);

        return weightsPath;
    }

    // The caller owns the HttpClient: building one here and disposing it on return would hand back a store whose
    // download client is already dead. Deletion never issues a request, but the store still needs one to construct.
    private static HuggingFaceImageModelStore Store(HttpClient http, string modelsDirectory, ImageModelRegistry registry)
    {
        return new HuggingFaceImageModelStore(GgufStoreTestInfrastructure.DownloadClient(http,
                GgufStoreTestInfrastructure.NoTokenStore(),
                GgufStoreTestInfrastructure.AbundantSpace(),
                GgufStoreTestInfrastructure.Options(modelsDirectory)),
            registry,
            ImageOptions(modelsDirectory),
            NullLogger<HuggingFaceImageModelStore>.Instance);
    }

    private static ImageModelStoreOptions ImageOptions(string modelsDirectory)
    {
        return new ImageModelStoreOptions
        {
            ModelsDirectory = modelsDirectory
        };
    }
}
