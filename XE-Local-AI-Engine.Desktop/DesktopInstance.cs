namespace XE_Local_AI_Engine.Desktop;

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

internal sealed class DesktopInstance : IAsyncDisposable
{
    private static DesktopInstance? _processInstance;
    private readonly FileStream _lease;
    private readonly CancellationTokenSource _stopping = new();
    private readonly NamedPipeServerStream _server;
    private Task _listener = Task.CompletedTask;
    private bool _listeningStopped;

    private DesktopInstance(FileStream lease, string pipeName)
    {
        _lease = lease;
        _server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    internal static string PipeName(string directory)
    {
        var normalized = DesktopStartupOptions.NormalizeDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return "xe-activate-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\n" + normalized)));
    }

    internal static DesktopInstance? TryAcquire(string directory)
    {
        FileStream lease;
        try
        {
            lease = new FileStream(Path.Combine(directory, "desktop-shell.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (DirectoryNotFoundException)
        {
            throw;
        }
        catch (PathTooLongException)
        {
            throw;
        }
        catch (IOException exception) when (!OperatingSystem.IsWindows() || (exception.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }

        try
        {
            return new DesktopInstance(lease, PipeName(directory));
        }
        catch
        {
#pragma warning disable MA0045 // This synchronous ownership factory must release the unopened lock lease before rethrowing construction failure.
            lease.Dispose();
#pragma warning restore MA0045
            throw;
        }
    }

    internal static void RetainUntilProcessExit(DesktopInstance instance)
    {
        if (_processInstance is not null && !ReferenceEquals(_processInstance, instance))
        {
            throw new InvalidOperationException("Only one desktop shell may own this process.");
        }

        // Updating is unsafe while this executable is loaded, even after its windows close.
        _processInstance = instance;
    }

    internal void Listen(Action activate) =>
        _listener = ListenAsync(activate, _stopping.Token);

    internal static async Task ActivateAsync(string directory, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var client = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        await client.WriteAsync(new byte[]
        {
            1
        }, timeout.Token);
        await client.FlushAsync(timeout.Token);
    }

    internal async Task StopListeningAsync()
    {
        if (_listeningStopped)
        {
            return;
        }

        await _stopping.CancelAsync();
        await _server.DisposeAsync();
        await _listener;
        _listeningStopped = true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopListeningAsync();
        await _lease.DisposeAsync();
        _stopping.Dispose();
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        var message = new byte[1];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _server.WaitForConnectionAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                if (await _server.ReadAsync(message, timeout.Token) == 1 && message[0] == 1)
                {
                    activate();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // An idle peer does not prevent the next activation.
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested && _server.IsConnected)
                {
                    _server.Disconnect();
                }
            }
        }
    }
}
