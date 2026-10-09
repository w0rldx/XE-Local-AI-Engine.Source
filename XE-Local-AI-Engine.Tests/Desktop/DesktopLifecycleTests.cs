namespace XE_Local_AI_Engine.Tests.Desktop;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
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
    public void StartupOptions_DebugIsOptInAndCombinesWithRestartArguments()
    {
        using var directory = new TempDirectory();
        var plain = DesktopStartupOptions.Parse(["--desktop", "--port", "35207"], null, directory.Path);
        AssertEx.False(plain.Debug);
        AssertEx.Equal(Path.Combine(plain.DataDirectory, "logs"), plain.LogsDirectory);

        var debug = DesktopStartupOptions.Parse(["--desktop", "--DEBUG", "--port", "35207"], null, directory.Path);
        AssertEx.True(debug.Debug);
        AssertEx.Equal(35207, debug.Port);
        AssertEx.True(DesktopStartupOptions.Parse(["--debug"], null, directory.Path).Debug);
        AssertEx.False(DesktopCommandLine.RunsEngine(["--debug"], launchMode: null), "--debug alone opens the window.");
    }

    [Test]
    public void OwnedStartInfo_DebugStartsTheEngineAtDebugLevel()
    {
        using var directory = new TempDirectory();
        var plain = DesktopEngineSession.CreateOwnedStartInfo(DesktopStartupOptions.Parse([], directory.Path, directory.Path), "pipe");
        var debug = DesktopEngineSession.CreateOwnedStartInfo(DesktopStartupOptions.Parse(["--debug"], directory.Path, directory.Path), "pipe");

        AssertEx.False(plain.Environment.TryGetValue(DesktopEngineSession.LogLevelVariable, out var inherited) && inherited == "Debug",
            "Debug off leaves the engine at its configured level.");
        AssertEx.Equal("Debug", debug.Environment[DesktopEngineSession.LogLevelVariable]);
        AssertEx.Equal("pipe", debug.Environment[DesktopEngineSession.LifetimePipeVariable]);
        AssertEx.True(debug.ArgumentList.Contains(DesktopEngineSession.DesktopArgument, StringComparer.Ordinal));
        AssertEx.False(debug.ArgumentList.Contains(DesktopStartupOptions.DebugArgument, StringComparer.OrdinalIgnoreCase),
            "The engine gets the level through its configuration, not the shell's flag.");
    }

    [Test]
    public async Task Echo_CopiesEngineOutputWhileReadinessAndErrorTailStillWork()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new StringBuilder();
        await using (var echo = new DesktopEngineEcho(output, error))
        {
            using var stdout = Reader("starting\nXE_READY=1 http://127.0.0.1:35207\n[DBG] request\n");
            using var stderr = Reader("fatal: port in use");
            await DesktopEngineSession.DrainOutputAsync(stdout, ready, echo, CancellationToken.None);
            await DesktopEngineSession.DrainErrorsAsync(stderr, tail, echo, CancellationToken.None);
        }

        AssertEx.True(ready.Task.IsCompletedSuccessfully, "The readiness line is still detected.");
        AssertEx.Equal("fatal: port in use", tail.ToString());
        AssertEx.Equal(string.Join(Environment.NewLine, "starting", "XE_READY=1 http://127.0.0.1:35207", "[DBG] request", string.Empty), output.ToString());
        AssertEx.Equal("fatal: port in use", error.ToString());
    }

    [Test]
    public async Task Echo_BlockedConsoleNeverBlocksTheDrainAndKeepsTheNewestLines()
    {
        var sink = new BlockingWriter();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new StringBuilder();
        var lineCount = DesktopEngineEcho.Capacity * 3;
        var lines = Enumerable.Range(0, lineCount).Select(i => "line " + i.ToString(CultureInfo.InvariantCulture)).ToList();
        lines.Insert(lineCount / 2, "XE_READY=1 http://127.0.0.1:35207");
        await using (var echo = new DesktopEngineEcho(sink, sink))
        {
            using var stdout = Reader(string.Join('\n', lines) + "\n");
            using var stderr = Reader(new string('e', DesktopEngineSession.ErrorTailLimit * 2) + "last words");
            // A bound, not a wait for an event: a drain that blocks on the held console never completes.
            await Task.WhenAll(DesktopEngineSession.DrainOutputAsync(stdout, ready, echo, CancellationToken.None),
                DesktopEngineSession.DrainErrorsAsync(stderr, tail, echo, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30));

            AssertEx.True(ready.Task.IsCompletedSuccessfully, "Readiness must not wait for the console.");
            AssertEx.True(tail.ToString().EndsWith("last words", StringComparison.Ordinal), "The error tail must not wait for the console.");
            sink.Release();
            await AssertEx.EventuallyAsync(() => sink.Written.Contains(lines[^1], StringComparer.Ordinal), TimeSpan.FromSeconds(30),
                "The newest line survives the backlog.");
        }

        var written = sink.Written.ToArray();
        AssertEx.True(written.Length <= DesktopEngineEcho.Capacity + 1, $"Drop-oldest bounds the backlog, wrote {written.Length}.");
        // The writer may hold one item from before the backlog filled; every other early line must have been dropped.
        var oldest = lines.Take(DesktopEngineEcho.Capacity).ToHashSet(StringComparer.Ordinal);
        AssertEx.True(written.Count(oldest.Contains) <= 1, "Old lines are dropped, not queued without bound.");
    }

    [Test]
    public void CommandLine_RoutesExplicitOperatorAndBrowserModesWithoutNativeUi()
    {
        AssertEx.False(DesktopCommandLine.RunsEngine([]));
        AssertEx.False(DesktopCommandLine.RunsEngine(["--DESKTOP", "--PORT=41234"]));
        AssertEx.True(DesktopCommandLine.RunsEngine(["--desktop", "--no-browser"]));
        foreach (var argument in new[]
                 {
                     "--browser",
                     "--HEADLESS",
                     "--no-browser",
                     "--mcp-only",
                     "--help",
                     "--status",
                     "--setup",
                     "--mcp-key=read",
                     "--reset-admin-password",
                     "--knowledge-downgrade-export"
                 })
        {
            AssertEx.True(DesktopCommandLine.RunsEngine([argument]), argument);
        }

        AssertEx.True(DesktopCommandLine.EngineArguments(["--browser"]).SequenceEqual(["--browser"]));
        AssertEx.True(DesktopCommandLine.EngineArguments(["--headless", "--port=41234"]).SequenceEqual(["--headless", "--port=41234"]));
        AssertEx.True(DesktopCommandLine.EngineArguments(["--mcp-only"]).SequenceEqual(["--mcp-only"]));
        _ = AssertEx.Throws<ArgumentException>(() => DesktopCommandLine.EngineArguments(["--browser", "--headless"]));
    }

    [Test]
    public void CommandLine_EnvironmentSelectedMcpOnlyBypassesTheWindowUnlessAnArgumentAsksForIt()
    {
        // The packaged default binary must keep the engine's unattended contract: XE_LAUNCH_MODE=mcp-only with no
        // mode argument runs the engine headless, and the engine (not the shell) resolves the mode from the variable.
        AssertEx.True(DesktopCommandLine.RunsEngine([], "mcp-only"));
        AssertEx.True(DesktopCommandLine.RunsEngine(["--port=41234"], "MCP-ONLY"));
        AssertEx.True(DesktopCommandLine.EngineArguments(["--port=41234"], "mcp-only").SequenceEqual(["--port=41234"]));

        AssertEx.False(DesktopCommandLine.RunsEngine(["--desktop"], "mcp-only"));
        AssertEx.True(DesktopCommandLine.EngineArguments(["--desktop"], "mcp-only").SequenceEqual(["--desktop"]));
        AssertEx.False(DesktopCommandLine.RunsEngine([], "desktop"));
        AssertEx.False(DesktopCommandLine.RunsEngine([], launchMode: null));
        AssertEx.True(DesktopCommandLine.EngineArguments([], "desktop").SequenceEqual(["--desktop"]));
    }

    [Test]
    public void CommandLine_RestartsPreserveStandaloneAndOwnedUiModes()
    {
        var arguments = new[]
        {
            "--desktop",
            "--no-browser"
        };
        var standalone = DesktopLaunch.BuildRestartArguments(arguments, LaunchMode.Desktop, port: null);
        var owned = DesktopLaunch.BuildRestartArguments(arguments, LaunchMode.Desktop, port: null, shellOwned: true);
        AssertEx.True(DesktopCommandLine.RunsEngine([.. standalone]));
        AssertEx.False(DesktopCommandLine.RunsEngine([.. owned]));
    }

    [Test]
    public void StartupOptions_RejectUnsafeDataAndUnknownOrInvalidArguments()
    {
        using var directory = new TempDirectory();
        foreach (var invalid in new[]
                 {
                     "relative",
                     directory.Path + "\n"
                 })
        {
            _ = AssertEx.Throws<ArgumentException>(() => DesktopStartupOptions.Parse([], invalid, directory.Path));
        }

        foreach (var args in new[]
                 {
                     new[]
                     {
                         "--port",
                         "0"
                     },
                     ["--port", "65536"],
                     ["--port"],
                     ["--port", "12", "--port", "13"],
                     ["--unknown"]
                 })
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
        foreach (var action in new[]
                 {
                     DesktopCloseAction.Tray,
                     DesktopCloseAction.Quit,
                     DesktopCloseAction.Ask
                 })
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
            await peer.WriteAsync(new byte[]
            {
                2
            }, deadline.Token);
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

        static string Status(bool running, string url, string data) =>
            JsonSerializer.Serialize(new
            {
                running,
                url,
                dataDir = data
            });

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
        var options = new DesktopStartupOptions
        {
            DataDirectory = directory.Path,
            ProfileDirectory = directory.Path,
            Origin = new Uri("http://127.0.0.1:35207")
        };
        await using var session = await DesktopEngineSession.StartAsync(options, CancellationToken.None);
        AssertEx.False(session.OwnsEngine);
        AssertEx.Null(session.WaitForEngineExitAsync());
        AssertEx.Null(session.EngineExitCode);
        await session.StopAsync();
        AssertEx.Equal(options.Origin, session.Origin);
    }

    [Test]
    public void ErrorTail_KeepsOnlyTheEnginesLastWordsAndSurvivesAnEmptyStream()
    {
        var tail = new StringBuilder();
        DesktopEngineSession.AppendTail(tail, string.Empty);
        AssertEx.Equal(0, tail.Length);

        const string LastWords = "You must install or update .NET";
        DesktopEngineSession.AppendTail(tail, new string('x', DesktopEngineSession.ErrorTailLimit));
        DesktopEngineSession.AppendTail(tail, LastWords);
        var kept = tail.ToString();
        AssertEx.Equal(DesktopEngineSession.ErrorTailLimit, kept.Length);
        AssertEx.Equal(new string('x', DesktopEngineSession.ErrorTailLimit - LastWords.Length) + LastWords, kept);
    }

    [Test]
    public void StartupFailure_ReportsTheEnginesErrorTailAndStaysReadableWithoutOne()
    {
        AssertEx.Equal("The engine exited before readiness.", DesktopEngineSession.DescribeStartupFailure(null));
        AssertEx.Equal("The engine exited before readiness.", DesktopEngineSession.DescribeStartupFailure("  \n "));
        AssertEx.Equal("The engine exited before readiness. It reported: You must install or update .NET",
            DesktopEngineSession.DescribeStartupFailure("  You must install or update .NET\n"));
    }

    [Test]
    public void StartupFailure_NamesTheEnginesExitCode()
    {
        AssertEx.Equal("The engine exited before readiness (exit code 8).", DesktopEngineSession.DescribeStartupFailure(null, 8));
        AssertEx.Equal("The engine exited before readiness (exit code 10). It reported: data directory not writable",
            DesktopEngineSession.DescribeStartupFailure("data directory not writable\n", 10));
    }

    [Test]
    public async Task OwnedStart_WhenTheEngineExitsBeforeReadiness_SurfacesItsExitCodeAndLastWords()
    {
        using var directory = new TempDirectory();
        var options = DesktopStartupOptions.Parse([], directory.Path, directory.Path);
        // A stand-in engine that writes its last words and exits 8 without ever announcing readiness.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe")
            {
                ArgumentList =
                {
                    "/c",
                    "echo node key refused 1>&2 & exit 8"
                }
            }
            : new ProcessStartInfo("/bin/sh")
            {
                ArgumentList =
                {
                    "-c",
                    "echo node key refused >&2; exit 8"
                }
            };
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        var owned = await DesktopEngineSession.StartOwnedAsync(options, start, "xe-desktop-test-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                                              .WaitAsync(TimeSpan.FromSeconds(60));

        AssertEx.Null(owned.Session);
        AssertEx.Equal(8, owned.ExitCode);
        AssertEx.Contains(AssertEx.NotNull(owned.ErrorTail), "node key refused");
    }

    private static StreamReader Reader(string text) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    /// <summary>A console with a text selection: every write waits until the test releases it.</summary>
    private sealed class BlockingWriter : TextWriter
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Written { get; } = new();
        public override Encoding Encoding => Encoding.UTF8;

        public void Release() =>
            _gate.TrySetResult();

        public override async Task WriteAsync(string? value)
        {
            await _gate.Task;
            Written.Enqueue(value ?? string.Empty);
        }

        public override Task WriteLineAsync(string? value) =>
            WriteAsync(value);
    }
}
