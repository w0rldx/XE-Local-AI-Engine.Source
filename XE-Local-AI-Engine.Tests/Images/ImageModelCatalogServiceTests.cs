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

    [Test]
    public async Task GetCatalogView_AnInstallMissingACatalogPart_ReadsAsNotInstalledSoInstallCanCompleteIt()
    {
        // Qwen-Image 2.1 installs made before the catalog gained the LlmVision projector carry three parts. Showing them as
        // installed would hide the only button that fetches the missing file (the store keeps the three it has).
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        ImageModelPartRole[] olderSet = [ImageModelPartRole.Diffusion, ImageModelPartRole.Vae, ImageModelPartRole.Llm];

        var older = await CreateServiceWith(profiler, [Entry("qwen-image-2.1", olderSet)]).GetCatalogViewAsync(CancellationToken.None);
        var complete = await CreateServiceWith(profiler, [Entry("qwen-image-2.1", [.. olderSet, ImageModelPartRole.LlmVision])])
            .GetCatalogViewAsync(CancellationToken.None);

        AssertEx.False(older.Entries.Single(static entry => entry.Entry.Id == "qwen-image-2.1").IsInstalled);
        AssertEx.True(complete.Entries.Single(static entry => entry.Entry.Id == "qwen-image-2.1").IsInstalled);
    }

    private static ImageModelCatalogService CreateService(IHardwareProfiler profiler, IReadOnlyList<string> installedNames)
    {
        return CreateServiceWith(profiler, [.. installedNames.Select(static name => Entry(name, ImageModelPartRole.Diffusion))]);
    }

    private static ImageModelRegistryEntry Entry(string name, params ImageModelPartRole[] roles)
    {
        return new ImageModelRegistryEntry
        {
            ModelName = name,
            RepoId = "owner/repo",
            Family = ImageModelFamily.Sd15,
            Kind = ImageModelKind.Txt2Img,
            Parts =
            [
                .. roles.Select(static role => new ImageModelPart
                {
                    Role = role,
                    FileName = $"{role}.gguf",
                    LocalPath = $"/m/{role}.gguf",
                    SizeBytes = 1
                })
            ],
            SizeBytes = 0,
            SourceRevision = "0000000",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch
        };
    }

    private static ImageModelCatalogService CreateServiceWith(IHardwareProfiler profiler, IReadOnlyList<ImageModelRegistryEntry> installed)
    {
        var registry = Substitute.For<IImageModelRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(installed));

        return new ImageModelCatalogService(new ImageModelCatalog(NullLogger<ImageModelCatalog>.Instance),
            registry,
            profiler,
            NullLogger<ImageModelCatalogService>.Instance);
    }
}
