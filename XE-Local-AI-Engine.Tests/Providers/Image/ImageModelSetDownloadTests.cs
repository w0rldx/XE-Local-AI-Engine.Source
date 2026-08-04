namespace XE_Local_AI_Engine.Tests.Providers.Image;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     An image model is a file <b>set</b>, and two behaviours only exist at set level. Progress must be reported
///     set-relative — forwarding each part's own byte counts unchanged makes the bar fill and snap back to zero once per
///     part, which reads as a failed restart. And a part already complete on disk must be reused, because the registry
///     entry is only written once the WHOLE set succeeds: without reuse a set that failed on its last part re-downloads
///     every earlier part from scratch, which for a multi-part diffusion model is tens of gigabytes of pointless
///     transfer. These tests drive both through the public <see cref="HuggingFaceImageModelStore.EnsureModelAsync" />
///     against a scripted handler, so they pin observable behaviour rather than the private helpers that implement it.
/// </summary>
public sealed class ImageModelSetDownloadTests
{
    private const string ModelName = "multi-part-model";
    private const string RepoId = "Qwen/Qwen-Image";
    private const string FirstFile = "diffusion.safetensors";
    private const string SecondFile = "vae.safetensors";

    private static readonly byte[] FirstBytes = Encoding.ASCII.GetBytes("0123456789");
    private static readonly byte[] SecondBytes = Encoding.ASCII.GetBytes("abcdef");

    [Test]
    public async Task EnsureModel_WithSizedParts_ReportsOneMonotonicBarAgainstTheSetTotal()
    {
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var handler = FileServingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        var store = Store(http, models.Path, registry);
        var progress = new RecordingProgress();

        _ = await store.EnsureModelAsync(SizedRequest(), progress, CancellationToken.None).ConfigureAwait(false);

        var reports = progress.Reports;
        AssertEx.NotEmpty(reports, "A two-part download must report progress.");

        // Every report is framed against the whole set: the same total, the same part count, and the model's own name
        // (the download client reports per-file, so an unadapted report would leak the file's own framing).
        foreach (var report in reports)
        {
            AssertEx.Equal(ModelName, report.ModelName);
            AssertEx.Equal(expected: 16L, report.TotalBytes ?? 0, "Every part must be reported against the set total, not its own length.");
            AssertEx.Equal(expected: 2, report.PartCount ?? 0);
        }

        // Set-relative offsetting: the byte count only ever advances. Un-offset part-2 reports would restart at 1.
        var completed = reports.Select(report => report.CompletedBytes ?? 0).ToList();
        for (var index = 1; index < completed.Count; index++)
        {
            AssertEx.True(completed[index] >= completed[index - 1],
                $"Set-relative progress must never go backwards: {completed[index - 1]} → {completed[index]}.");
        }

        AssertEx.Equal(expected: 16L, completed[^1], "The last report must account for every byte in the set.");

        // The part index names the file being fetched, and it advances exactly once — the whole point of "part 2 of 3".
        AssertEx.Contains(reports, report => report.PartIndex == 1);
        AssertEx.Contains(reports, report => report.PartIndex == 2);
        var firstPartTwo = reports.FindIndex(report => report.PartIndex == 2);
        AssertEx.True(reports.Take(firstPartTwo).All(report => report.PartIndex == 1),
            "The part index must advance monotonically; a report for part 1 after part 2 began would reorder the bar.");
        // Part 2's first set-relative count already includes every byte of part 1.
        AssertEx.True(reports[firstPartTwo].CompletedBytes >= 10L,
            "Part 2's first report must be offset by the bytes part 1 already transferred.");
    }

    [Test]
    public async Task EnsureModel_WhenAPartDeclaresNoSize_NeverReportsAFabricatedSetTotal()
    {
        // A set total is only honest when EVERY part declared a size. Summing the known ones would report a total the
        // transfer overshoots, and a bar that passes 100% is worse than one that admits it cannot compute a percentage.
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var handler = FileServingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        var store = Store(http, models.Path, registry);
        var progress = new RecordingProgress();

        var request = Request(Part(FirstFile, sizeBytes: 10), Part(SecondFile, sizeBytes: null));
        _ = await store.EnsureModelAsync(request, progress, CancellationToken.None).ConfigureAwait(false);

        AssertEx.NotEmpty(progress.Reports, "A two-part download must report progress.");
        AssertEx.False(progress.Reports.Any(report => report.TotalBytes == 16L),
            "With one part's size unknown the set total is not computable and must never be invented.");
        // The framing that IS honest still rides every report, so the UI can still say which file is transferring.
        AssertEx.True(progress.Reports.All(report => report.PartCount == 2),
            "An unknown total must not cost the part framing.");
    }

    [Test]
    public async Task EnsureModel_WhenAnEarlierPartIsAlreadyCompleteOnDisk_ReusesItInsteadOfRefetching()
    {
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var handler = FileServingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        var store = Store(http, models.Path, registry);

        // Exactly what a run that failed on its last part leaves behind: part 1 final (not .part), no registry entry.
        await SeedPartAsync(models.Path, FirstFile, FirstBytes).ConfigureAwait(false);

        var handle = await store.EnsureModelAsync(SizedRequest(), progress: null, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, handler.CallCount, "The completed part must be reused; only the missing part may be fetched.");
        AssertEx.Equal(expected: 2, handle.Parts.Count);
        var reused = handle.Parts.Single(part => part.FileName == FirstFile);
        AssertEx.Equal(expected: 10L, reused.SizeBytes, "The reused part's size must come from the file actually on disk.");
    }

    [Test]
    public async Task EnsureModel_WhenTheExistingPartHasADifferentSize_RedownloadsItRatherThanTrustingIt()
    {
        // Length is the only cheap check available (hashing a 13 GB weight would cost a large fraction of the transfer
        // it avoids), so a length that disagrees with the declared size means the file is truncated or stale — the one
        // case where reusing it would silently install a corrupt model.
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var handler = FileServingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        var store = Store(http, models.Path, registry);

        await SeedPartAsync(models.Path, FirstFile, Encoding.ASCII.GetBytes("truncated")).ConfigureAwait(false);

        var handle = await store.EnsureModelAsync(SizedRequest(), progress: null, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, handler.CallCount, "A size mismatch must force a re-download, never a silent reuse.");
        AssertEx.Equal(expected: 10L, handle.Parts.Single(part => part.FileName == FirstFile).SizeBytes);
    }

    [Test]
    public async Task EnsureModel_WhenTheExistingPartDeclaredNoSize_RedownloadsItBecauseNothingCanVerifyIt()
    {
        // Without a declared size a truncated leftover is indistinguishable from a complete file, so there is nothing to
        // check against and the part is re-downloaded — the behaviour that predates reuse.
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var handler = FileServingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        var store = Store(http, models.Path, registry);

        await SeedPartAsync(models.Path, FirstFile, FirstBytes).ConfigureAwait(false);

        var request = Request(Part(FirstFile, sizeBytes: null), Part(SecondFile, sizeBytes: 6));
        _ = await store.EnsureModelAsync(request, progress: null, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, handler.CallCount, "An undeclared size leaves nothing to verify against, so the part must be re-fetched.");
    }

    [Test]
    public async Task EnsureModel_WhenNoPartIsOnDisk_DownloadsEveryPart()
    {
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        using var handler = FileServingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        var store = Store(http, models.Path, registry);

        var handle = await store.EnsureModelAsync(SizedRequest(), progress: null, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, handler.CallCount, "With nothing on disk every part must be fetched.");
        AssertEx.Equal(expected: 2, handle.Parts.Count);
    }

    [Test]
    public async Task EnsureModel_WhenAPartNamesItsOwnRepo_FetchesThatPartFromThatRepo()
    {
        // A file-set is not always published in one place. Qwen-Image is the case that forced this: the quantized
        // diffusion transformer and the VAE ship in one repo while the Qwen2.5-VL text encoder ships in another, so with
        // one set-level repo id the model cannot be installed at all. Each part therefore resolves its own source.
        using var models = new GgufStoreTestInfrastructure.TempModelsDir();
        var requestedUrls = new List<string>();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            lock (requestedUrls)
            {
                requestedUrls.Add(url);
            }

            var body = url.Contains(SecondFile, StringComparison.Ordinal) ? SecondBytes : FirstBytes;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        using var registry = new ImageModelRegistry(ImageOptions(models.Path), NullLogger<ImageModelRegistry>.Instance);
        var store = Store(http, models.Path, registry);

        const string otherRepo = "mradermacher/Qwen2.5-VL-7B-Instruct-GGUF";
        var request = Request(
            Part(FirstFile, sizeBytes: 10),
            new ImageModelPartRequest { Role = ImageModelPartRole.Llm, FileName = SecondFile, SizeBytes = 6, RepoId = otherRepo });

        var handle = await store.EnsureModelAsync(request, progress: null, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, handle.Parts.Count);
        var diffusionUrl = requestedUrls.Single(url => url.Contains(FirstFile, StringComparison.Ordinal));
        var encoderUrl = requestedUrls.Single(url => url.Contains(SecondFile, StringComparison.Ordinal));
        AssertEx.Contains(diffusionUrl, RepoId, StringComparison.Ordinal, "The un-overridden part must come from the set's repo.");
        AssertEx.Contains(encoderUrl, otherRepo, StringComparison.Ordinal, "The overriding part must come from the repo it named.");
        AssertEx.False(encoderUrl.Contains(RepoId, StringComparison.Ordinal), "A part's own repo must replace the set's, not be appended to it.");
    }

    // Serves each part's bytes by file name so a test can assert exactly which parts were fetched.
    private static GgufStoreTestInfrastructure.ScriptedHandler FileServingHandler()
    {
        return new GgufStoreTestInfrastructure.ScriptedHandler(static (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            byte[]? body = null;
            if (path.EndsWith(SecondFile, StringComparison.Ordinal))
            {
                body = SecondBytes;
            }
            else if (path.EndsWith(FirstFile, StringComparison.Ordinal))
            {
                body = FirstBytes;
            }

            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body)
                };
        });
    }

    // Writes a part exactly where the store expects it, as a completed (not .part) file.
    private static async Task SeedPartAsync(string modelsDirectory, string fileName, byte[] content)
    {
        var directory = Path.Combine(modelsDirectory, HuggingFaceImageModelStore.SafeModelDirectorySegment(ModelName));
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, fileName), content).ConfigureAwait(false);
    }

    private static ImageModelRequest SizedRequest()
    {
        return Request(Part(FirstFile, sizeBytes: 10), Part(SecondFile, sizeBytes: 6));
    }

    private static ImageModelPartRequest Part(string fileName, long? sizeBytes)
    {
        return new ImageModelPartRequest
        {
            Role = fileName == FirstFile ? ImageModelPartRole.Diffusion : ImageModelPartRole.Vae,
            FileName = fileName,
            SizeBytes = sizeBytes
        };
    }

    private static ImageModelRequest Request(params ImageModelPartRequest[] parts)
    {
        return new ImageModelRequest
        {
            ModelName = ModelName,
            RepoId = RepoId,
            Family = ImageModelFamily.Sdxl,
            Parts = parts
        };
    }

    private static ImageModelStoreOptions ImageOptions(string modelsDirectory)
    {
        return new ImageModelStoreOptions
        {
            ModelsDirectory = modelsDirectory
        };
    }

    private static HuggingFaceImageModelStore Store(HttpClient http, string modelsDirectory, ImageModelRegistry registry)
    {
        var downloadClient = GgufStoreTestInfrastructure.DownloadClient(http,
            GgufStoreTestInfrastructure.NoTokenStore(),
            GgufStoreTestInfrastructure.AbundantSpace(),
            GgufStoreTestInfrastructure.Options(modelsDirectory));

        return new HuggingFaceImageModelStore(downloadClient,
            registry,
            ImageOptions(modelsDirectory),
            NullLogger<HuggingFaceImageModelStore>.Instance);
    }

    private sealed class RecordingProgress : IProgress<PullProgress>
    {
        private readonly List<PullProgress> _reports = [];

        public List<PullProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(PullProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }
}
