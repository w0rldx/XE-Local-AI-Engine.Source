namespace XE_Local_AI_Engine.Tests.Providers.Image;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     F-031: the download client creates the model's destination directory before it issues the first request, so a
///     weight file that does not exist (404) used to leave an orphan empty folder under the image-models directory that
///     nothing ever cleaned up. These tests pin both halves of the fix: the failure surfaces as a sanitized
///     <see cref="HuggingFaceDownloadException" />, and the empty directory is removed — while a resumable partial
///     download is deliberately preserved.
/// </summary>
public sealed class ImageModelStoreFailedDownloadTests
{
    private const string ModelName = "bogus-model";

    [Test]
    public async Task EnsureModel_WhenTheWeightFileIsNotFound_ThrowsAndLeavesNoOrphanDirectory()
    {
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);

        var store = new HuggingFaceImageModelStore(DownloadClient(http, models.Path),
            registry,
            ImageOptions(models.Path),
            NullLogger<HuggingFaceImageModelStore>.Instance);

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(Request(), progress: null, CancellationToken.None))
                          .ConfigureAwait(false);

        var modelDirectory = Path.Combine(models.Path, ModelName);
        AssertEx.False(Directory.Exists(modelDirectory), "A failed download must not leave an orphan empty model directory behind.");
    }

    [Test]
    public async Task EnsureModel_WhenAPartialDownloadExists_KeepsTheDirectorySoTheNextAttemptCanResume()
    {
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);

        // Seed a partial transfer, exactly as an interrupted earlier attempt would have left it.
        var modelDirectory = Path.Combine(models.Path, ModelName);
        _ = Directory.CreateDirectory(modelDirectory);
        var partPath = Path.Combine(modelDirectory, "weights.safetensors.part");
        await File.WriteAllTextAsync(partPath, "half-a-file").ConfigureAwait(false);

        var store = new HuggingFaceImageModelStore(DownloadClient(http, models.Path),
            registry,
            ImageOptions(models.Path),
            NullLogger<HuggingFaceImageModelStore>.Instance);

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(Request(), progress: null, CancellationToken.None))
                          .ConfigureAwait(false);

        AssertEx.True(Directory.Exists(modelDirectory), "Cleanup must not delete a directory that still holds resumable bytes.");
        AssertEx.True(File.Exists(partPath), "The partial file must survive so the next attempt resumes from it.");
    }

    private static ImageModelRequest Request()
    {
        return new ImageModelRequest
        {
            ModelName = ModelName,
            RepoId = "Comfy-Org/stable-diffusion-v1-5-archive",
            Family = ImageModelFamily.Sd15,
            Parts = [new ImageModelPartRequest { Role = ImageModelPartRole.Diffusion, FileName = "weights.safetensors" }]
        };
    }

    private static ImageModelStoreOptions ImageOptions(string modelsDirectory)
    {
        return new ImageModelStoreOptions
        {
            ModelsDirectory = modelsDirectory
        };
    }

    private static HfDownloadClient DownloadClient(HttpClient http, string modelsDirectory)
    {
        return GgufStoreTestInfrastructure.DownloadClient(http,
            GgufStoreTestInfrastructure.NoTokenStore(),
            GgufStoreTestInfrastructure.AbundantSpace(),
            GgufStoreTestInfrastructure.Options(modelsDirectory));
    }
}
