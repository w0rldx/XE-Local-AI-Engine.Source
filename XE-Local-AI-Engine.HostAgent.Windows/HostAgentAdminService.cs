namespace XE_Local_AI_Engine.HostAgent.Windows;

using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Windows.Implementation;
using XE_Local_AI_Engine.HostAgent.Windows.Wsl;
using XE_Local_AI_Engine.HostAgent.Windows.Wsl.Implementation;

public sealed class HostAgentAdminService : IDisposable
{
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);
    private readonly DesiredStateStore _desiredStateStore;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private readonly IHostAgentLinuxClient _linuxClient;
    private readonly HostAgentWindowsPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly Wsl2Driver _wslDriver;

    public HostAgentAdminService(IHostAgentLinuxClient linuxClient,
        Wsl2Driver wslDriver,
        DesiredStateStore desiredStateStore,
        HostAgentWindowsPaths paths,
        TimeProvider timeProvider)
    {
        _linuxClient = linuxClient;
        _wslDriver = wslDriver;
        _desiredStateStore = desiredStateStore;
        _paths = paths;
        _timeProvider = timeProvider;
    }

    public void Dispose()
    {
        _lifecycleLock.Dispose();
    }

    public async Task<HostAgentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var desiredState = await _desiredStateStore.GetDesiredStateAsync(cancellationToken).ConfigureAwait(false);
        var linuxStatus = string.Equals(desiredState, DesiredStateStore.Stopped, StringComparison.Ordinal)
            ? null
            : await _linuxClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return new HostAgentStatus(ResolveHostState(desiredState, linuxStatus),
            desiredState,
            linuxStatus?.WebUiUrl ?? string.Empty,
            ResolveComponentHealth(linuxStatus, "ollama"),
            ResolveComponentHealth(linuxStatus, "web-server"),
            _timeProvider.GetUtcNow());
    }

    public Task<IReadOnlyList<string>> ReadLogsAsync(int tail, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTail = Math.Clamp(tail, 1, 5_000);
        if (!Directory.Exists(_paths.LogDirectory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var lines = Directory.EnumerateFiles(_paths.LogDirectory, "host-agent-*.log", SearchOption.TopDirectoryOnly)
                             .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                             .Take(5)
                             .Reverse()
                             .SelectMany(ReadLogFileLines)
                             .TakeLast(normalizedTail)
                             .ToArray();

        return Task.FromResult<IReadOnlyList<string>>(lines);
    }

    public async Task<HostAgentAdminActionResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ShutdownCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<HostAgentAdminActionResult> StartupAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartupCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<HostAgentAdminActionResult> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var shutdown = await ShutdownCoreAsync(cancellationToken).ConfigureAwait(false);
            var startup = await StartupCoreAsync(cancellationToken).ConfigureAwait(false);

            return new HostAgentAdminActionResult(DesiredStateStore.Running,
                [.. shutdown.Diagnostics, .. startup.Diagnostics]);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<HostAgentAdminActionResult> ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        await _desiredStateStore.SetDesiredStateAsync(DesiredStateStore.Stopped, cancellationToken).ConfigureAwait(false);

        var stopReport = await _linuxClient.StopAllContainersAsync(DefaultDrainTimeout, cancellationToken).ConfigureAwait(false);
        diagnostics.Add(stopReport?.Succeeded == true ? "containers_stopped" : "containers_stop_unavailable");

        var stopUnit = await _wslDriver.StopUserUnitAsync(cancellationToken).ConfigureAwait(false);
        diagnostics.Add(stopUnit.Succeeded ? "host_agent_linux_unit_stopped" : "host_agent_linux_unit_stop_failed");

        if (OperatingSystem.IsWindows())
        {
            var terminate = await _wslDriver.TerminateAsync(cancellationToken).ConfigureAwait(false);
            diagnostics.Add(terminate.Succeeded ? "wsl_terminated" : "wsl_terminate_failed");
        }

        return new HostAgentAdminActionResult(DesiredStateStore.Stopped, diagnostics);
    }

    private async Task<HostAgentAdminActionResult> StartupCoreAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        await _desiredStateStore.SetDesiredStateAsync(DesiredStateStore.Running, cancellationToken).ConfigureAwait(false);

        await _wslDriver.ColdStartAsync(cancellationToken).ConfigureAwait(false);
        diagnostics.Add("host_agent_linux_unit_started");

        var startReport = await _linuxClient.StartAllContainersAsync(cancellationToken).ConfigureAwait(false);
        diagnostics.Add(startReport?.Succeeded == true ? "containers_started" : "containers_start_unavailable");

        return new HostAgentAdminActionResult(DesiredStateStore.Running, diagnostics);
    }

    private static IEnumerable<string> ReadLogFileLines(string path)
    {
        try
        {
            return File.ReadLines(path).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ResolveHostState(string desiredState, HostAgentStatusReply? linuxStatus)
    {
        if (string.Equals(desiredState, DesiredStateStore.Stopped, StringComparison.Ordinal))
        {
            return "stopped";
        }

        return linuxStatus is null ? "degraded" : "running";
    }

    private static string ResolveComponentHealth(HostAgentStatusReply? linuxStatus, string componentName)
    {
        var component = linuxStatus?.Components.FirstOrDefault(component =>
            string.Equals(component.Name, componentName, StringComparison.OrdinalIgnoreCase));

        return component?.Health.ToString() switch
        {
            null => "unknown",
            "ContainerHealthStarting" => "starting",
            "ContainerHealthHealthy" => "healthy",
            "ContainerHealthUnhealthy" => "unhealthy",
            "ContainerHealthStopped" => "stopped",
            _ => "unknown"
        };
    }
}

public sealed record HostAgentAdminActionResult(string DesiredState, IReadOnlyList<string> Diagnostics);

public sealed record HostAgentLogTail(IReadOnlyList<string> Lines);
