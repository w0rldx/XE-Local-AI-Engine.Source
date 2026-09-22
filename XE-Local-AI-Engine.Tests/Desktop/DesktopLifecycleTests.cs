namespace XE_Local_AI_Engine.Tests.Desktop;

using System.IO.Pipes;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class DesktopLifecycleTests
{
    [Test]
    public void StartupOptions_DefaultToPerUserDataAndPreserveRestartPort()
    {
        using var directory = new TempDirectory();
        var options = DesktopStartupOptions.Parse(["--desktop", "--no-browser", "--port", "35207"], null, directory.Path);
        AssertEx.Equal(Path.Combine(directory.Path, "XE-Local-AI-Engine"), options.DataDirectory);
        AssertEx.Equal(Path.Combine(options.DataDirectory, "desktop-profile"), options.ProfileDirectory);
        AssertEx.Equal(35207, options.Port);
        AssertEx.Null(options.Origin);
    }

    [Test]
    public void StartupOptions_AcceptsCaseInsensitiveFlagsAndEqualsPort()
    {
        using var directory = new TempDirectory();
        var options = DesktopStartupOptions.Parse(["--DESKTOP", "--NO-BROWSER", "--PORT=41234"], null, directory.Path);
        AssertEx.Equal(41234, options.Port);
        options = DesktopStartupOptions.Parse(["--Desktop", "--PoRt", "41235"], null, directory.Path);
        AssertEx.Equal(41235, options.Port);
    }

    [Test]
    public void CommandLine_RoutesExplicitOperatorAndBrowserModesWithoutNativeUi()
    {
        AssertEx.False(DesktopCommandLine.RunsEngine([]));
        AssertEx.False(DesktopCommandLine.RunsEngine(["--DESKTOP", "--PORT=41234"]));
        AssertEx.True(DesktopCommandLine.RunsEngine(["--desktop", "--no-browser"]));
        foreach (var argument in new[] { "--browser", "--HEADLESS", "--no-browser", "--mcp-only", "--help", "--status", "--setup", "--mcp-key=read", "--reset-admin-password", "--knowledge-downgrade-export" })
        {
            AssertEx.True(DesktopCommandLine.RunsEngine([argument]), argument);
        }

        AssertEx.True(DesktopCommandLine.EngineArguments(["--browser"]).SequenceEqual(["--browser"]));
        AssertEx.True(DesktopCommandLine.EngineArguments(["--headless", "--port=41234"]).SequenceEqual(["--headless", "--port=41234"]));
        AssertEx.True(DesktopCommandLine.EngineArguments(["--mcp-only"]).SequenceEqual(["--mcp-only"]));
        _ = AssertEx.Throws<ArgumentException>(() => DesktopCommandLine.EngineArguments(["--browser", "--headless"]));
    }

    [Test]
    public void CommandLine_RestartsPreserveStandaloneAndOwnedUiModes()
    {
        var arguments = new[] { "--desktop", "--no-browser" };
        var standalone = DesktopLaunch.BuildRestartArguments(arguments, LaunchMode.Desktop, port: null);
        var owned = DesktopLaunch.BuildRestartArguments(arguments, LaunchMode.Desktop, port: null, shellOwned: true);
        AssertEx.True(DesktopCommandLine.RunsEngine([.. standalone]));
        AssertEx.False(DesktopCommandLine.RunsEngine([.. owned]));
    }

    [Test]
    public void StartupOptions_RejectUnsafeDataAndUnknownOrInvalidArguments()
    {
        using var directory = new TempDirectory();
        foreach (var invalid in new[] { "relative", directory.Path + "\n" })
        {
            _ = AssertEx.Throws<ArgumentException>(() => DesktopStartupOptions.Parse([], invalid, directory.Path));
        }

        foreach (var args in new[] { new[] { "--port", "0" }, ["--port", "65536"], ["--port"], ["--port", "12", "--port", "13"], ["--unknown"] })
        {
            _ = AssertEx.Throws<ArgumentException>(() => DesktopStartupOptions.Parse(args, null, directory.Path));
        }
    }

    [Test]
    public void StartupOptions_ExplicitProbeKeepsProfileAndOrigin()
    {
        using var directory = new TempDirectory();
        var options = DesktopStartupOptions.Parse(["--origin", "http://127.0.0.1:35207", "--profile-dir", directory.Path], null, directory.Path);
        AssertEx.Equal(directory.Path, options.ProfileDirectory);
        AssertEx.Equal("http://127.0.0.1:35207/", options.Origin!.AbsoluteUri);
    }

    [Test]
    public async Task Preferences_RoundTripAndResetWithoutHidingWhenTrayUnavailable()
    {
        using var directory = new TempDirectory();
        AssertEx.Equal(DesktopCloseAction.Ask, await DesktopPreferences.ReadAsync(directory.Path, CancellationToken.None));
        foreach (var action in new[] { DesktopCloseAction.Tray, DesktopCloseAction.Quit, DesktopCloseAction.Ask })
        {
            await DesktopPreferences.WriteAsync(directory.Path, action, CancellationToken.None);
            AssertEx.Equal(action, await DesktopPreferences.ReadAsync(directory.Path, CancellationToken.None));
        }

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "desktop-close.txt"), "corrupt");
        AssertEx.Equal(DesktopCloseAction.Ask, await DesktopPreferences.ReadAsync(directory.Path, CancellationToken.None));
        AssertEx.Equal(DesktopCloseAction.Ask, DesktopPreferences.Resolve(DesktopCloseAction.Tray, trayAvailable: false));
        AssertEx.Equal(DesktopCloseAction.Tray, DesktopPreferences.Resolve(DesktopCloseAction.Tray, trayAvailable: true));
        AssertEx.Equal(DesktopCloseAction.Quit, DesktopPreferences.Resolve(DesktopCloseAction.Quit, trayAvailable: false));
    }

    [Test]
    public async Task Instance_SecondLaunchActivatesFirstAndLeaseIsReleased()
    {
        using var directory = new TempDirectory();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = DesktopInstance.TryAcquire(directory.Path);
        AssertEx.NotNull(first);
        await using (first!)
        {
            AssertEx.Null(DesktopInstance.TryAcquire(directory.Path));
            first!.Listen(() => received.TrySetResult());
            await DesktopInstance.ActivateAsync(directory.Path, CancellationToken.None);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertEx.True(received.Task.IsCompletedSuccessfully);
        }

        await using var replacement = DesktopInstance.TryAcquire(directory.Path);
        AssertEx.NotNull(replacement);
    }

    [Test]
    public async Task Instance_StoppingActivationRetainsLeaseUntilDisposal()
    {
        using var directory = new TempDirectory();
        var instance = DesktopInstance.TryAcquire(directory.Path);
        AssertEx.NotNull(instance);
        await using (instance!)
        {
            instance!.Listen(() => { });
            await instance.StopListeningAsync();
            AssertEx.Null(DesktopInstance.TryAcquire(directory.Path));
        }

        await using var replacement = DesktopInstance.TryAcquire(directory.Path);
        AssertEx.NotNull(replacement);
    }

    [Test]
    public async Task Instance_InvalidMessageDoesNotActivateAndNextPeerCanActivate()
    {
        using var directory = new TempDirectory();
        await using var first = DesktopInstance.TryAcquire(directory.Path);
        AssertEx.NotNull(first);
        var activations = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first!.Listen(() =>
        {
            Interlocked.Increment(ref activations);
            received.TrySetResult();
        });
        await using (var peer = new NamedPipeClientStream(".", DesktopInstance.PipeName(directory.Path), PipeDirection.Out,
                         PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await peer.ConnectAsync(deadline.Token);
            await peer.WriteAsync(new byte[] { 2 }, deadline.Token);
            await peer.FlushAsync(deadline.Token);
        }

        await DesktopInstance.ActivateAsync(directory.Path, CancellationToken.None);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEx.Equal(1, Volatile.Read(ref activations));
    }

    [Test]
    public void Instance_IdentityNormalizesTrailingSeparatorsAndMissingRootFails()
    {
        using var directory = new TempDirectory();
        AssertEx.Equal(DesktopInstance.PipeName(directory.Path), DesktopInstance.PipeName(directory.Path + Path.DirectorySeparatorChar));
        _ = AssertEx.Throws<DirectoryNotFoundException>(() => DesktopInstance.TryAcquire(Path.Combine(directory.Path, "missing")));
    }

    [Test]
    public void Status_RequiresRunningLoopbackAndMatchingDataRoot()
    {
        using var directory = new TempDirectory();
        static string Status(bool running, string url, string data) => JsonSerializer.Serialize(new { running, url, dataDir = data });
        AssertEx.Null(DesktopEngineSession.ParseStatus(Status(false, "http://127.0.0.1:35207", directory.Path), directory.Path));
        AssertEx.Equal("http://127.0.0.1:35207/", DesktopEngineSession.ParseStatus(Status(true, "http://127.0.0.1:35207", directory.Path), directory.Path)!.AbsoluteUri);
        _ = AssertEx.Throws<ArgumentException>(() => DesktopEngineSession.ParseStatus(Status(true, "https://example.com", directory.Path), directory.Path));
        _ = AssertEx.Throws<InvalidDataException>(() => DesktopEngineSession.ParseStatus(Status(true, "http://127.0.0.1:35207", Path.Combine(directory.Path, "other")), directory.Path));
    }

    [Test]
    public void ExistingEngine_MustHonorExplicitPortPin()
    {
        var origin = new Uri("http://127.0.0.1:35207");
        DesktopEngineSession.ValidateRequestedPort(origin, 35207);
        DesktopEngineSession.ValidateRequestedPort(origin, null);
        _ = AssertEx.Throws<InvalidOperationException>(() => DesktopEngineSession.ValidateRequestedPort(origin, 35208));
    }

    [Test]
    public async Task ExplicitAttach_HasNoOwnedProcessOrShutdownAuthority()
    {
        using var directory = new TempDirectory();
        var options = new DesktopStartupOptions { DataDirectory = directory.Path, ProfileDirectory = directory.Path, Origin = new Uri("http://127.0.0.1:35207") };
        await using var session = await DesktopEngineSession.StartAsync(options, CancellationToken.None);
        AssertEx.False(session.OwnsEngine);
        AssertEx.Null(session.EngineExited);
        AssertEx.Null(session.EngineExitCode);
        await session.StopAsync();
        AssertEx.Equal(options.Origin, session.Origin);
    }
}
