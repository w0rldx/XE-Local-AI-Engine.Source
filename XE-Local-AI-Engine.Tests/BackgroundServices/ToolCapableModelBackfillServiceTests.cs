namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The startup backfill feeding installed models into the tool-capable allow-list is best-effort: it runs the
///     registrar once and swallows (logged) an unreadable registry or settings file, since a stale list beats a node
///     that will not start.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ToolCapableModelBackfillServiceTests
{
    [Test]
    public async Task ExecuteAsync_RunsTheBackfillExactlyOnce()
    {
        var registrar = Substitute.For<IToolCapableModelRegistrar>();
        _ = registrar.BackfillInstalledAsync(Arg.Any<CancellationToken>()).Returns(3);
        var logger = new RecordingLogger<ToolCapableModelBackfillService>();
        using var service = new ToolCapableModelBackfillService(registrar, logger);

        await BackgroundServiceTestHelper.RunExecuteAsync(service, CancellationToken.None);

        _ = await registrar.Received(1).BackfillInstalledAsync(Arg.Any<CancellationToken>());
        AssertEx.Empty(logger.Entries);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheRegistrarThrows_LogsAndDoesNotFaultTheHost()
    {
        var registrar = Substitute.For<IToolCapableModelRegistrar>();
        _ = registrar.BackfillInstalledAsync(Arg.Any<CancellationToken>())
                     .ThrowsAsync(new IOException("the installed-model descriptor directory is unreadable"));
        var logger = new RecordingLogger<ToolCapableModelBackfillService>();
        using var service = new ToolCapableModelBackfillService(registrar, logger);

        await BackgroundServiceTestHelper.RunExecuteAsync(service, CancellationToken.None);

        AssertEx.True(logger.HasEntry(LogLevel.Warning, "Could not backfill the tool-capable model list"),
            "A failed backfill must be reported, not silently dropped.");
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelledDuringStartup_StopsQuietly()
    {
        var registrar = Substitute.For<IToolCapableModelRegistrar>();
        _ = registrar.BackfillInstalledAsync(Arg.Any<CancellationToken>())
                     .ThrowsAsync(new OperationCanceledException());
        var logger = new RecordingLogger<ToolCapableModelBackfillService>();
        using var service = new ToolCapableModelBackfillService(registrar, logger);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellation.Token);

        // Shutdown during startup is not a fault, so it must not be reported as one.
        AssertEx.Empty(logger.Entries);
    }
}
