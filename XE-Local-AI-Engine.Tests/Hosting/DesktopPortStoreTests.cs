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
public sealed class DesktopPortStoreTests
{
    [Test]
    public void ResolveBindUrl_WhenPortFileMissing_FallsBackToDynamicBind()
    {
        using var directory = new TempDirectory();

        var resolved = DesktopPortStore.ResolveBindUrl(directory.Path);

        AssertEx.Equal(DesktopLaunch.LoopbackBindUrl, resolved);
    }

    [Test]
    public void ResolveBindUrl_WhenPortFileMalformedOrOutOfRange_FallsBackToDynamicBind()
    {
        // Non-numeric, zero, privileged (<= 1024), and above the max port all reject to the dynamic :0 bind.
        foreach (var invalid in new[] { "abc", "0", "1024", "70000", "-1", "  " })
        {
            using var directory = new TempDirectory();
            WritePortFile(directory.Path, invalid);

            var resolved = DesktopPortStore.ResolveBindUrl(directory.Path);

            AssertEx.Equal(DesktopLaunch.LoopbackBindUrl, resolved);
        }
    }

    [Test]
    public void ResolveBindUrl_WhenPersistedPortIsFree_RebindsThatPort()
    {
        using var directory = new TempDirectory();
        var freePort = FindFreeLoopbackPort();
        WritePortFile(directory.Path, freePort.ToString(CultureInfo.InvariantCulture));

        var resolved = DesktopPortStore.ResolveBindUrl(directory.Path);

        AssertEx.Equal($"http://{DesktopLaunch.LoopbackHost}:{freePort.ToString(CultureInfo.InvariantCulture)}", resolved);
    }

    [Test]
    public void ResolveBindUrl_WhenPersistedPortIsTaken_FallsBackToDynamicBind()
    {
        using var directory = new TempDirectory();
        var port = FindFreeLoopbackPort();

        // Hold the port for the duration of the resolve so the availability probe sees it as taken.
        using var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();

        WritePortFile(directory.Path, port.ToString(CultureInfo.InvariantCulture));

        var resolved = DesktopPortStore.ResolveBindUrl(directory.Path);

        AssertEx.Equal(DesktopLaunch.LoopbackBindUrl, resolved);
    }

    [Test]
    public void PersistThenResolve_RoundTripsToTheSameLoopbackUrl()
    {
        using var directory = new TempDirectory();
        var freePort = FindFreeLoopbackPort();

        DesktopPortStore.Persist(directory.Path, freePort, NullLogger.Instance);
        var resolved = DesktopPortStore.ResolveBindUrl(directory.Path);

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
