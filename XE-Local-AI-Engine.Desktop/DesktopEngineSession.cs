namespace XE_Local_AI_Engine.Desktop;

using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;

internal sealed class DesktopEngineSession : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly NamedPipeServerStream? _lifetime;
    private readonly CancellationTokenSource _reading = new();
    private Task _output = Task.CompletedTask;
    private Task _errors = Task.CompletedTask;
    private bool _stopped;
    private bool _disposed;
    private Uri? _origin;

    private DesktopEngineSession(Uri? origin, Process? process = null, NamedPipeServerStream? lifetime = null)
    {
        _origin = origin;
        _process = process;
        _lifetime = lifetime;
    }

    internal Uri Origin => _origin ?? throw new InvalidOperationException("The engine is not ready.");
    internal bool OwnsEngine => _process is not null;
    internal Task? EngineExited => _process?.WaitForExitAsync(CancellationToken.None);
    internal int? EngineExitCode => _process is { HasExited: true } ? _process.ExitCode : null;

    internal static async Task<DesktopEngineSession> StartAsync(DesktopStartupOptions options, CancellationToken cancellationToken)
    {
        if (options.Origin is not null)
        {
            return new DesktopEngineSession(options.Origin);
        }

        var existing = await DiscoverAsync(options.DataDirectory, cancellationToken);
        if (existing is not null)
        {
            ValidateRequestedPort(existing, options.Port);
            return new DesktopEngineSession(existing);
        }

        var owned = await StartOwnedAsync(options, cancellationToken);
        if (owned is not null)
        {
            return owned;
        }

        existing = await DiscoverAsync(options.DataDirectory, cancellationToken);
        if (existing is null) { throw new InvalidOperationException("The engine exited before readiness."); }
        ValidateRequestedPort(existing, options.Port);
        return new DesktopEngineSession(existing);
    }

    private static async Task<DesktopEngineSession?> StartOwnedAsync(DesktopStartupOptions options, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(options.DataDirectory);
        var pipeName = "xe-desktop-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        start.ArgumentList.Add("--desktop");
        start.ArgumentList.Add("--no-browser");
        if (options.Port is { } port)
        {
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        }

        start.Environment["XE_DESKTOP_LIFETIME_PIPE"] = pipeName;
        var process = new Process { StartInfo = start };
        DesktopEngineSession? session = null;
        try
        {
#pragma warning disable CA2000 // Async ownership: this try/finally disposes failures; successful return transfers the session after clearing the local.
            session = new DesktopEngineSession(null, process, pipe);
#pragma warning restore CA2000
            if (!process.Start())
            {
                throw new InvalidOperationException("The engine could not start.");
            }

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session._output = DrainOutputAsync(process.StandardOutput, ready, session._reading.Token);
            session._errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, session._reading.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var startup = Task.WhenAll(pipe.WaitForConnectionAsync(deadline.Token), ready.Task.WaitAsync(deadline.Token));
            var exited = process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAny(startup, exited).WaitAsync(deadline.Token);
            if (process.HasExited || ready.Task.IsFaulted)
            {
                await exited.WaitAsync(deadline.Token);
                await deadline.CancelAsync();
                try
                {
                    await startup;
                }
                catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
                {
                    // The candidate lost a startup race or exited before readiness.
                }

                return null;
            }

            await startup;
            session._origin = await ReadOwnedReadyAsync(options.DataDirectory, process.Id, deadline.Token);
            var ownedSession = session;
            session = null;
            return ownedSession;
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string dataDirectory)
    {
        var directory = AppContext.BaseDirectory;
        var dll = Path.Combine(directory, "XE-Local-AI-Engine.Client.dll");
        var apphost = Path.Combine(directory, "XE-Local-AI-Engine.Client");
        var start = new ProcessStartInfo
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (!OperatingSystem.IsWindows() && File.Exists(apphost))
        {
            start.FileName = apphost;
        }
        else
        {
            if (!File.Exists(dll))
            {
                throw new FileNotFoundException("The engine payload is missing.");
            }

            start.FileName = ResolveDotNetHost();
            start.ArgumentList.Add(dll);
        }

        start.Environment["XE_DATA_DIR"] = dataDirectory;
        if (OperatingSystem.IsLinux())
        {
            start.Environment["XE_DESKTOP_SUPERVISOR_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        }

        start.Environment.Remove("XE_DESKTOP_LIFETIME_PIPE");
        return start;
    }

    internal static void ValidateRequestedPort(Uri origin, int? requestedPort)
    {
        if (requestedPort is { } port && origin.Port != port)
        {
            throw new InvalidOperationException("The running engine does not use the requested port.");
        }
    }

    internal static Uri? ParseStatus(string json, string dataDirectory)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("running").GetBoolean())
        {
            return null;
        }

        return ValidateOrigin(root.GetProperty("url").GetString(), root.GetProperty("dataDir").GetString(), dataDirectory);
    }

    internal static Uri ValidateOrigin(string? url, string? actualDirectory, string expectedDirectory)
    {
        if (actualDirectory is null || !string.Equals(DesktopStartupOptions.NormalizeDirectory(actualDirectory),
                DesktopStartupOptions.NormalizeDirectory(expectedDirectory),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("The engine data directory does not match.");
        }

        return DesktopLaunchOptions.Parse(["--origin", url ?? string.Empty, "--profile-dir", expectedDirectory]).Origin;
    }

    internal async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        if (_lifetime is not null)
        {
            await _lifetime.DisposeAsync();
        }
        if (_process is null)
        {
            _stopped = true;
            return;
        }

        try
        {
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(40), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Process.Start can fail before an owned process exists.
        }

        _stopped = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopAsync();
        }
        finally
        {
            try
            {
                await _reading.CancelAsync();
                await Task.WhenAll(_output, _errors).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception)
            {
                // Stream teardown must not hide the original shutdown failure or retain process handles.
            }
            finally
            {
                _process?.Dispose();
                _reading.Dispose();
                _disposed = true;
            }
        }
    }

    private static string ResolveDotNetHost()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), name, StringComparison.OrdinalIgnoreCase))
        {
            return Environment.ProcessPath!;
        }

        var candidate = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") : null
        }.Where(root => !string.IsNullOrEmpty(root)).Select(root => Path.Combine(root!, name)).FirstOrDefault(File.Exists);
        return candidate ?? name;
    }

    internal static async Task<Uri?> DiscoverAsync(string directory, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(directory);
        start.ArgumentList.Add("--status");
        start.ArgumentList.Add("--json");
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("Engine discovery failed.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            var json = await output;
            if (process.ExitCode is not (0 or 1) || json.Length > 65536)
            {
                throw new InvalidDataException("Engine discovery failed.");
            }

            return ParseStatus(json, directory);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        }
    }

    private static async Task DrainOutputAsync(StreamReader reader, TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("XE_READY=1 ", StringComparison.Ordinal))
                {
                    ready.TrySetResult();
                }
            }
        }
        finally
        {
            ready.TrySetException(new InvalidOperationException("The engine exited before readiness."));
        }
    }

    private static async Task<Uri> ReadOwnedReadyAsync(string directory, int processId, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(directory, "ready.json"));
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.GetProperty("pid").GetInt32() != processId)
        {
            throw new InvalidDataException("The engine readiness process does not match.");
        }

        return ValidateOrigin(root.GetProperty("url").GetString(), root.GetProperty("dataDir").GetString(), directory);
    }
}
