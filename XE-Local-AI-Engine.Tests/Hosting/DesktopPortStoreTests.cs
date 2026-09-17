namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the desktop loopback-port persistence store. Verifies the best-effort read path (missing,
///     malformed, out-of-range, taken → dynamic <c>:0</c> bind), the happy-path re-bind of a remembered free port, and
///     the persist round-trip. Each test uses an isolated temp directory and never touches the real per-user data dir.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DesktopPortStoreTests
{
    [Test]
    public async Task ResolveBindUrl_WhenPortFileMissing_FallsBackToDynamicBind()
    {
        using var directory = new TempDirectory();

        var resolved = await DesktopPortStore.ResolveBindUrlAsync(directory.Path).ConfigureAwait(false);

        AssertEx.Equal(DesktopLaunch.LoopbackBindUrl, resolved);
    }

    [Test]
    public async Task ResolveBindUrl_WhenPortFileMalformedOrOutOfRange_FallsBackToDynamicBind()
    {
        // Non-numeric, zero, privileged (<= 1024), and above the max port all reject to the dynamic :0 bind.
        foreach (var invalid in new[]
                 {
                     "abc",
                     "0",
                     "1024",
                     "70000",
                     "-1",
                     "  "
                 })
        {
            using var directory = new TempDirectory();
            WritePortFile(directory.Path, invalid);

            var resolved = await DesktopPortStore.ResolveBindUrlAsync(directory.Path).ConfigureAwait(false);

            AssertEx.Equal(DesktopLaunch.LoopbackBindUrl, resolved);
        }
    }

    [Test]
    public async Task ResolveBindUrl_WhenPersistedPortIsFree_RebindsThatPort()
    {
        using var directory = new TempDirectory();
        var freePort = FindFreeLoopbackPort();
        WritePortFile(directory.Path, freePort.ToString(CultureInfo.InvariantCulture));

        var resolved = await DesktopPortStore.ResolveBindUrlAsync(directory.Path).ConfigureAwait(false);

        AssertEx.Equal($"http://{DesktopLaunch.LoopbackHost}:{freePort.ToString(CultureInfo.InvariantCulture)}", resolved);
    }

    [Test]
    public async Task ResolveBindUrl_WhenPersistedPortIsTaken_FallsBackToDynamicBind()
    {
        using var directory = new TempDirectory();
        var port = FindFreeLoopbackPort();

        // Hold the port for the duration of the resolve so the availability probe sees it as taken.
        using var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();

        WritePortFile(directory.Path, port.ToString(CultureInfo.InvariantCulture));

        var resolved = await DesktopPortStore.ResolveBindUrlAsync(directory.Path).ConfigureAwait(false);

        AssertEx.Equal(DesktopLaunch.LoopbackBindUrl, resolved);
    }

    [Test]
    public async Task PersistThenResolve_RoundTripsToTheSameLoopbackUrl()
    {
        using var directory = new TempDirectory();
        var freePort = FindFreeLoopbackPort();

        DesktopPortStore.Persist(directory.Path, freePort, NullLogger.Instance);
        var resolved = await DesktopPortStore.ResolveBindUrlAsync(directory.Path).ConfigureAwait(false);

        AssertEx.Equal($"http://{DesktopLaunch.LoopbackHost}:{freePort.ToString(CultureInfo.InvariantCulture)}", resolved);
    }

    [Test]
    public void Persist_WhenDirectoryDoesNotExist_DoesNotThrow()
    {
        // A non-existent data directory must not abort startup; the write failure is swallowed (best-effort).
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"xe-port-missing-{Guid.NewGuid():N}");

        DesktopPortStore.Persist(missingDirectory, 50000, NullLogger.Instance);

        // Reaching here without an exception is the assertion.
        AssertEx.True(true);
    }

    [Test]
    public async Task ReadyFile_PersistsRoundTripsAndDeletes()
    {
        using var directory = new TempDirectory();
        var info = new ReadyInfo("1.2.3", "http://127.0.0.1:41234",
            "http://127.0.0.1:41234/api/local/v1/mcp/server", directory.Path, 123, DateTimeOffset.UnixEpoch);

        DesktopPortStore.PersistReady(directory.Path, info, NullLogger.Instance);

        AssertEx.Equal(info, AssertEx.NotNull(await DesktopPortStore.ReadReadyAsync(directory.Path).ConfigureAwait(false)));
        DesktopPortStore.DeleteReady(directory.Path, NullLogger.Instance);
        AssertEx.Null(await DesktopPortStore.ReadReadyAsync(directory.Path).ConfigureAwait(false));
        DesktopPortStore.DeleteReady(directory.Path, NullLogger.Instance);
    }

    [Test]
    public async Task ReadyEvidence_DistinguishesAbsentFromInvalidAndRejectsUnsafeUris()
    {
        using var directory = new TempDirectory();
        AssertEx.Equal(ReadyEvidenceState.Absent, (await DesktopPortStore.ReadReadyEvidenceAsync(directory.Path).ConfigureAwait(false)).State);

        foreach (var invalidJson in new[]
                 {
                     "{not-json",
                     $$"""{"version":"1.0.0","url":"http://example.com:41234","mcpUrl":"http://example.com:41234/api/local/v1/mcp/server","dataDir":"{{directory.Path}}","pid":123,"startedAtUtc":"1970-01-01T00:00:00Z"}""",
                     $$"""{"version":"1.0.0","url":"http://127.0.0.1:41234","mcpUrl":"http://127.0.0.1:41234/evil","dataDir":"{{directory.Path}}","pid":123,"startedAtUtc":"1970-01-01T00:00:00Z"}""",
                     $$"""{"version":"1.0.0","url":"http://127.0.0.1:41234","mcpUrl":"http://127.0.0.1:41234/api/local/v1/mcp/server","dataDir":"{{directory.Path}}","pid":0,"startedAtUtc":"1970-01-01T00:00:00Z"}"""
                 })
        {
            await File.WriteAllTextAsync(Path.Combine(directory.Path, DesktopPortStore.ReadyFileName), invalidJson).ConfigureAwait(false);
            var evidence = await DesktopPortStore.ReadReadyEvidenceAsync(directory.Path).ConfigureAwait(false);
            AssertEx.Equal(ReadyEvidenceState.Invalid, evidence.State);
            AssertEx.Null(evidence.Info);
        }
    }

    [Test]
    public void ReadyDataDirectoryComparison_UsesOperatingSystemPathSemantics()
    {
        const string recorded = "/Users/Agent/XE-Data";
        const string differentlyCased = "/users/agent/xe-data";

        AssertEx.True(DesktopPortStore.ReadyDataDirectoriesEqual(recorded, differentlyCased, isWindows: true));
        AssertEx.False(DesktopPortStore.ReadyDataDirectoriesEqual(recorded, differentlyCased, isWindows: false));
        AssertEx.True(DesktopPortStore.ReadyDataDirectoriesEqual(recorded, recorded, isWindows: false));
    }

    [Test]
    public void IsPortAvailable_ReportsHeldAndReleasedPorts()
    {
        var port = FindFreeLoopbackPort();
        using var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();
        AssertEx.False(DesktopPortStore.IsPortAvailable(port));
        holder.Stop();
        AssertEx.True(DesktopPortStore.IsPortAvailable(port));
    }

    private static void WritePortFile(string directory, string content)
    {
        File.WriteAllText(Path.Combine(directory, DesktopPortStore.PortFileName), content);
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xe-port-store-{Guid.NewGuid():N}");
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of the temp directory.
            }
        }
    }
}
