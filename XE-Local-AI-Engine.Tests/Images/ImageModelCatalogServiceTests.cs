namespace XE_Local_AI_Engine.Tests.Images;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Images.Catalog;
using XE_Local_AI_Engine.Client.Services.Images.Catalog.Implementation;
using XE_Local_AI_Engine.Client.Services.Images.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The catalog view joins the bundled catalog with the installed registry and the hardware fit. A failed hardware
///     probe degrades the fit to Unknown instead of failing the catalog; a caller's own cancellation still propagates.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ImageModelCatalogServiceTests
{
    [Test]
    public async Task GetCatalogView_WhenProfilingFails_ServesEveryEntryWithAnUnknownFit()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("probe failed"));
        var service = CreateService(profiler, []);

        var view = await service.GetCatalogViewAsync(CancellationToken.None);

        AssertEx.NotEmpty(view.Entries);
        foreach (var entry in view.Entries)
        {
            AssertEx.Equal(ImageModelFitVerdict.Unknown, entry.Fit.Verdict);
            AssertEx.Equal(entry.Entry.Parts.Sum(static part => part.SizeBytes), entry.Fit.TotalBytes);
        }
    }

    [Test]
    public async Task GetCatalogView_WhenTheCallerCancels_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var service = CreateService(profiler, []);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => service.GetCatalogViewAsync(cancellation.Token));
    }

    [Test]
    public async Task GetCatalogView_MarksInstalledEntriesByCaseInsensitiveModelName()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        var service = CreateService(profiler, ["SD-1.5"]);

        var view = await service.GetCatalogViewAsync(CancellationToken.None);

        AssertEx.True(view.Entries.Single(static entry => entry.Entry.Id == "sd-1.5").IsInstalled);
        AssertEx.True(view.Entries.Where(static entry => entry.Entry.Id != "sd-1.5").All(static entry => !entry.IsInstalled));
    }

    private static ImageModelCatalogService CreateService(IHardwareProfiler profiler, IReadOnlyList<string> installedNames)
    {
        var registry = Substitute.For<IImageModelRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ImageModelRegistryEntry>>(
                [
                    .. installedNames.Select(static name => new ImageModelRegistryEntry
                    {
                        ModelName = name,
                        RepoId = "owner/repo",
                        Family = ImageModelFamily.Sd15,
                        Kind = ImageModelKind.Txt2Img,
                        Parts = [],
                        SizeBytes = 0,
                        SourceRevision = "0000000",
                        DownloadedAtUtc = DateTimeOffset.UnixEpoch
                    })
                ]));

        return new ImageModelCatalogService(new ImageModelCatalog(NullLogger<ImageModelCatalog>.Instance),
            registry,
            profiler,
            NullLogger<ImageModelCatalogService>.Instance);
    }
}
