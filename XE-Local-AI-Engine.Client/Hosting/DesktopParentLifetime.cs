namespace XE_Local_AI_Engine.Client.Hosting;

using System.IO.Pipes;

/// <summary>Monitors shell ownership before bootstrap and bounds shutdown after parent loss.</summary>
internal sealed class DesktopParentLifetime : IAsyncDisposable
{
    internal const string EnvironmentVariable = "XE_DESKTOP_LIFETIME_PIPE";
    internal static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(40);
    private readonly NamedPipeClientStream _pipe;
    private readonly TimeProvider _timeProvider;
    private readonly Action<int> _terminate;
    private readonly int _connectTimeoutMilliseconds;
    private readonly CancellationTokenSource _reading = new();
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _parentLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _monitor = Task.CompletedTask;
    private IHostApplicationLifetime? _lifetime;
    private CancellationTokenRegistration _hostStopping;
    private int _stopRequested;
    private bool _disposed;

    internal DesktopParentLifetime(string pipeName, TimeProvider timeProvider, Action<int> terminate,
        int connectTimeoutMilliseconds = 10_000)
    {
        ValidatePipeName(pipeName);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(terminate);
        ArgumentOutOfRangeException.ThrowIfNegative(connectTimeoutMilliseconds);
        _timeProvider = timeProvider;
        _terminate = terminate;
        _connectTimeoutMilliseconds = connectTimeoutMilliseconds;
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    internal Task ParentLost => _parentLost.Task;
    internal Task Completion => _monitor;

    internal static string? TakeEnvironmentValue()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, null);
        return value;
    }

    internal static string? ResolvePipeName(LaunchMode mode, string? value)
    {
        if (mode != LaunchMode.Desktop || value is null)
        {
            return null;
        }

        ValidatePipeName(value);
        return value;
    }

    internal static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrEmpty(pipeName) || pipeName.Length > 128
                                           || pipeName.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new ArgumentException("The desktop parent pipe name is invalid.", nameof(pipeName));
        }
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _pipe.ConnectAsync(_connectTimeoutMilliseconds, cancellationToken);
        _monitor = MonitorAsync();
    }

    internal void Bind(IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        bool parentLost;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Interlocked.CompareExchange(ref _lifetime, lifetime, null) is not null)
            {
                throw new InvalidOperationException("The desktop parent lifetime is already bound.");
            }

            _hostStopping = lifetime.ApplicationStopping.Register(_reading.Cancel);
            parentLost = _parentLost.Task.IsCompleted;
        }

        if (parentLost)
        {
            RequestGracefulStop();
        }
    }

    private async Task MonitorAsync()
    {
        try
        {
            _ = await _pipe.ReadAsync(new byte[1], _reading.Token);
        }
        catch (Exception exception) when (_reading.IsCancellationRequested
                                          && exception is OperationCanceledException or ObjectDisposedException or IOException)
        {
            return;
        }
        catch (IOException)
        {
            // A broken connection and EOF both end ownership.
        }

        Task escalation;
        lock (_gate)
        {
            if (_disposed || _reading.IsCancellationRequested)
            {
                return;
            }

            // Once parent loss wins, even DI disposal must not disarm the own-process exit deadline.
            escalation = TerminateAfterGraceAsync();
            _parentLost.TrySetResult();
        }

        RequestGracefulStop();
        await escalation;
    }

    private void RequestGracefulStop()
    {
        var lifetime = Volatile.Read(ref _lifetime);
        if (lifetime is not null && Interlocked.Exchange(ref _stopRequested, 1) == 0)
        {
            lifetime.StopApplication();
        }
    }

    private async Task TerminateAfterGraceAsync()
    {
        await Task.Delay(ShutdownGrace, _timeProvider, CancellationToken.None);
        _terminate(1);
    }

    public async ValueTask DisposeAsync()
    {
        bool parentLost;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            parentLost = _parentLost.Task.IsCompleted;
        }

        await _hostStopping.DisposeAsync();
        await _reading.CancelAsync();
        await _pipe.DisposeAsync();
        if (!parentLost)
        {
            await _monitor;
        }

        _reading.Dispose();
    }
}
