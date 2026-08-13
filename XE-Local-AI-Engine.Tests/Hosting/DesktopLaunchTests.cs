namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the self-contained desktop launcher host hooks. Pure tests only: the
///     desktop-mode gate, browser-command builder, loopback URL resolver, the signal→StopApplication seam, and the
///     non-fatal browser-launch path. No real process, signal, or network is exercised.
/// </summary>
public sealed class DesktopLaunchTests
{
    [Test]
    public void DesktopModeGate_WhenFlagUnset_LeavesPipelineUnchanged()
    {
        // No CLI arg, no env signal → desktop mode is off, so Program.cs keeps the standard HTTPS/HSTS pipeline.
        var isDesktop = DesktopLaunch.IsDesktopMode([], static _ => null);

        AssertEx.False(isDesktop);
    }

    [Test]
    public void DesktopModeGate_WhenEnvOrArgSet_EnablesDesktopPath()
    {
        var viaEnv = DesktopLaunch.IsDesktopMode([],
            static name => name == DesktopLaunch.LaunchModeEnvironmentVariable ? "desktop" : null);

        var viaEnvCaseInsensitive = DesktopLaunch.IsDesktopMode([],
            static name => name == DesktopLaunch.LaunchModeEnvironmentVariable ? "DESKTOP" : null);

        var viaArg = DesktopLaunch.IsDesktopMode(["--desktop"], static _ => null);

        AssertEx.True(viaEnv, "XE_LAUNCH_MODE=desktop must enable desktop mode.");
        AssertEx.True(viaEnvCaseInsensitive, "XE_LAUNCH_MODE is matched case-insensitively.");
        AssertEx.True(viaArg, "--desktop must enable desktop mode.");
    }

    [Test]
    public void DesktopModeGate_WhenVelopackManagedInstall_EnablesDesktopPathWithoutEnvOrArg()
    {
        // The Velopack stub launches the bare exe with neither the env var nor the --desktop arg, so the managed-install
        // signal alone must enable desktop mode — otherwise the packaged build never derives the node-sqlite connection
        // string and crashes applying migrations at startup.
        var isDesktop = DesktopLaunch.IsDesktopMode([], static _ => null, isManagedInstall: true);

        AssertEx.True(isDesktop, "A Velopack-managed install must enter desktop mode without an env/arg.");
    }

    [Test]
    public void DesktopModeGate_WhenNotManagedAndNoEnvOrArg_StaysOff()
    {
        // A raw-exe / dev / Aspire / CI run is not a Velopack install and sets no env/arg: the pipeline stays byte-
        // identical (HTTPS/HSTS, no loopback override, no browser launch).
        var isDesktop = DesktopLaunch.IsDesktopMode([], static _ => null, isManagedInstall: false);

        AssertEx.False(isDesktop);
    }

    [Test]
    public void ResetAdminPassword_WhenFlagAbsent_IsNotRequested()
    {
        var requested = DesktopLaunch.TryGetResetAdminPassword(["--desktop"], out var password);

        AssertEx.False(requested, "Without the flag the reset path must not run; the web host starts normally.");
        AssertEx.Null(password);
    }

    [Test]
    public void ResetAdminPassword_ReadsValueFromEitherSpaceOrEqualsForm()
    {
        var viaSpace = DesktopLaunch.TryGetResetAdminPassword(["--reset-admin-password", "N3w!Passw0rd123"], out var spaceValue);
        var viaEquals = DesktopLaunch.TryGetResetAdminPassword(["--reset-admin-password=N3w!Passw0rd123"], out var equalsValue);

        AssertEx.True(viaSpace);
        AssertEx.Equal("N3w!Passw0rd123", spaceValue);
        AssertEx.True(viaEquals, "The =form must be recognized so it is never mistaken for a normal launch.");
        AssertEx.Equal("N3w!Passw0rd123", equalsValue);
    }

    [Test]
    public void ResetAdminPassword_WhenFlagHasNoValue_IsRequestedButPasswordless()
    {
        // Flag present but no password supplied (trailing flag, or bare =): requested=true so Program.cs prints a usage
        // error and exits rather than silently booting the server with a half-typed command.
        var trailing = DesktopLaunch.TryGetResetAdminPassword(["--reset-admin-password"], out var trailingValue);
        var bareEquals = DesktopLaunch.TryGetResetAdminPassword(["--reset-admin-password="], out var bareEqualsValue);

        AssertEx.True(trailing);
        AssertEx.Null(trailingValue);
        AssertEx.True(bareEquals);
        AssertEx.Null(bareEqualsValue);
    }

    [Test]
    public void KnowledgeDowngradeCommand_RecognizesPreflightAndExport()
    {
        AssertEx.Equal(KnowledgeDowngradeCommand.None, DesktopLaunch.GetKnowledgeDowngradeCommand(["--desktop"]));
        AssertEx.Equal(KnowledgeDowngradeCommand.Preflight,
            DesktopLaunch.GetKnowledgeDowngradeCommand(["--knowledge-downgrade-preflight"]));
        AssertEx.Equal(KnowledgeDowngradeCommand.Export,
            DesktopLaunch.GetKnowledgeDowngradeCommand(["--knowledge-downgrade-export"]));
    }

    [Test]
    public void KnowledgeDowngradeCommand_WhenBothFlagsArePresent_RejectsAmbiguousAction()
    {
        _ = AssertEx.Throws<ArgumentException>(() => DesktopLaunch.GetKnowledgeDowngradeCommand(
            ["--knowledge-downgrade-preflight", "--knowledge-downgrade-export"]));
    }

    [Test]
    public void BrowserLaunchCommand_PerOs_UsesExplorerOrXdgOpen()
    {
        const string url = "http://127.0.0.1:5001/";

        var (windowsFile, windowsArgs) = BrowserLauncher.BuildOpenCommand(url, isWindows: true);
        var (linuxFile, linuxArgs) = BrowserLauncher.BuildOpenCommand(url, isWindows: false);

        AssertEx.Equal("explorer", windowsFile);
        AssertEx.Equal(url, windowsArgs);
        AssertEx.Equal("xdg-open", linuxFile);
        AssertEx.Equal(url, linuxArgs);

        // The launch must never shell-execute (repo convention guarded by CodexAuthServiceTests). Assert the intent
        // through the ProcessStartInfo the launcher constructs; do NOT assert on process exit code (explorer returns 1
        // on success).
        ProcessStartInfo? captured = null;
        BrowserLauncher.OpenBrowser(url, isWindows: true, NullLogger.Instance, startInfo => captured = startInfo);

        var startInfo = AssertEx.NotNull(captured);
        AssertEx.False(startInfo.UseShellExecute, "Browser launch must keep UseShellExecute=false.");
        AssertEx.Equal("explorer", startInfo.FileName);
    }

    [Test]
    public void LoopbackUrlResolver_ParsesServerAddressesFeature()
    {
        var feature = new FakeServerAddressesFeature();
        feature.Addresses.Add("http://127.0.0.1:54321");

        var resolved = LoopbackUrlResolver.Resolve(feature.Addresses);

        AssertEx.Equal("http://127.0.0.1:54321/", AssertEx.NotNull(resolved));
    }

    [Test]
    public void LoopbackUrlResolver_RewritesWildcardHostToLoopback()
    {
        // Kestrel can report a wildcard host for the bound listener; the resolver targets the loopback interface.
        var resolved = LoopbackUrlResolver.Resolve(["http://0.0.0.0:5000"]);

        AssertEx.Equal("http://127.0.0.1:5000/", AssertEx.NotNull(resolved));
    }

    [Test]
    public void DesktopLifecycle_SignalTrigger_CallsStopApplication()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        using var server = new FakeServer(new FakeServerAddressesFeature());
        using var lifecycle = new DesktopLifecycle(lifetime, server, NullLogger<DesktopLifecycle>.Instance);

        // Drive the graceful-stop seam directly — no real SIGHUP / console-ctrl event.
        lifecycle.TriggerGracefulStop();

        AssertEx.Equal(expected: 1, lifetime.StopApplicationCallCount);
    }

    [Test]
    public void BrowserLaunch_WhenProcessStartThrows_IsNonFatal()
    {
        const string url = "http://127.0.0.1:5005/";

        // The launch action throws (e.g. xdg-open absent); OpenBrowser must swallow it and not propagate.
        BrowserLauncher.OpenBrowser(url, isWindows: false, NullLogger.Instance,
            static _ => throw new InvalidOperationException("xdg-open not found"));

        // Reaching here without an exception is the assertion: a failed launch does not abort startup.
        AssertEx.True(true);
    }

    private sealed class FakeServerAddressesFeature : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; } = new List<string>();

        public bool PreferHostingUrls { get; set; }
    }

    private sealed class FakeServer : IServer
    {
        public FakeServer(IServerAddressesFeature addresses)
        {
            var features = new FeatureCollection();
            features.Set(addresses);
            Features = features;
        }

        public IFeatureCollection Features { get; }

        public void Dispose()
        {
            // No unmanaged state; the interface requires IDisposable.
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application,
            CancellationToken cancellationToken)
            where TContext : notnull
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopped = new();
        private readonly CancellationTokenSource _stopping = new();

        public int StopApplicationCallCount { get; private set; }

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopApplicationCallCount++;
        }
    }
}
