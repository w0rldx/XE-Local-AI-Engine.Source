namespace XE_Local_AI_Engine.Tests.Providers.Image;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ImageModelRegistryTests
{
    [Test]
    public async Task ImageModelRegistry_FileSet_ResolvesAllParts()
    {
        using var dir = new TempDir();
        var flux = WriteFileSet(dir.Path,
            ("flux1-schnell.gguf", ImageModelPartRole.Diffusion),
            ("ae.safetensors", ImageModelPartRole.Vae),
            ("clip_l.safetensors", ImageModelPartRole.ClipL),
            ("t5xxl.gguf", ImageModelPartRole.T5));

        using var registry = new ImageModelRegistry(Options(dir.Path), NullLogger<ImageModelRegistry>.Instance);
        await registry.UpsertAsync(flux, CancellationToken.None);

        var found = AssertEx.NotNull(await registry.FindAsync(flux.ModelName, CancellationToken.None));
        AssertEx.Equal(expected: 4, found.Parts.Count);
        AssertEx.Contains(found.Parts, part => part.Role == ImageModelPartRole.Diffusion);
        AssertEx.Contains(found.Parts, part => part.Role == ImageModelPartRole.Vae);
        AssertEx.Contains(found.Parts, part => part.Role == ImageModelPartRole.ClipL);
        AssertEx.Contains(found.Parts, part => part.Role == ImageModelPartRole.T5);
        AssertEx.True(found.Parts.All(part => File.Exists(part.LocalPath)));
    }

    [Test]
    public async Task ImageModelRegistry_DropsEntry_WhenAnyPartFileMissing()
    {
        using var dir = new TempDir();
        var sd15 = WriteFileSet(dir.Path, ("sd15.gguf", ImageModelPartRole.Diffusion));

        using var registry = new ImageModelRegistry(Options(dir.Path), NullLogger<ImageModelRegistry>.Instance);
        await registry.UpsertAsync(sd15, CancellationToken.None);

        // A missing part must exclude the whole set — an incomplete model is not resolvable.
        File.Delete(sd15.Parts[0].LocalPath);

        AssertEx.Null(await registry.FindAsync(sd15.ModelName, CancellationToken.None));
        AssertEx.Empty(await registry.ListAsync(CancellationToken.None));
    }

    private static ImageModelRegistryEntry WriteFileSet(string root, params (string FileName, ImageModelPartRole Role)[] files)
    {
        var parts = new List<ImageModelPart>(files.Length);
        long total = 0;
        foreach (var (fileName, role) in files)
        {
            var localPath = Path.Combine(root, fileName);
            File.WriteAllText(localPath, $"weights-of-{fileName}");
            var size = new FileInfo(localPath).Length;
            total += size;
            parts.Add(new ImageModelPart { Role = role, FileName = fileName, LocalPath = localPath, SizeBytes = size });
        }

        return new ImageModelRegistryEntry
        {
            ModelName = "leejet/FLUX.1-schnell-gguf",
            RepoId = "leejet/FLUX.1-schnell-gguf",
            Family = ImageModelFamily.Flux,
            Kind = ImageModelKind.Txt2Img,
            Parts = parts,
            SizeBytes = total,
            SourceRevision = "main",
            DownloadedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static ImageModelStoreOptions Options(string path)
    {
        return new ImageModelStoreOptions { ModelsDirectory = path };
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-image-reg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
