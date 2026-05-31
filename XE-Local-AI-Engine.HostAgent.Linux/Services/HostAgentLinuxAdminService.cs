namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;
using XE_Local_AI_Engine.HostAgent.Linux.Models;

/// <summary>
///     Application service for host agent linux admin behavior.
/// </summary>
public sealed class HostAgentLinuxAdminService
{
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);
    private readonly BootstrapModelReadinessService _bootstrapModelReadinessService;

    private readonly ContainerLifecycleService _containerLifecycleService;
    private readonly TimeProvider _timeProvider;
    private string _desiredState = "running";

    public HostAgentLinuxAdminService(ContainerLifecycleService containerLifecycleService,
        BootstrapModelReadinessService bootstrapModelReadinessService,
        TimeProvider timeProvider)
    {
        _containerLifecycleService = containerLifecycleService ?? throw new ArgumentNullException(nameof(containerLifecycleService));
        _bootstrapModelReadinessService = bootstrapModelReadinessService ?? throw new ArgumentNullException(nameof(bootstrapModelReadinessService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<HostAgentLinuxAdminStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var containers = await _containerLifecycleService.ListContainersAsync(cancellationToken).ConfigureAwait(false);
        var readiness = _bootstrapModelReadinessService.GetSnapshot();

        return new HostAgentLinuxAdminStatus(string.Equals(_desiredState, "stopped", StringComparison.Ordinal) ? "stopped" : "running",
            _desiredState,
            ResolveWebUiUrl(),
            ResolveComponentHealth(containers, "ollama"),
            ResolveComponentHealth(containers, "xe-node-web-server", "web-server"),
            _timeProvider.GetUtcNow(),
            readiness.IsReady,
            readiness.Diagnostics);
    }

    public async Task<HostAgentLinuxAdminActionResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        _desiredState = "stopped";
        var report = await _containerLifecycleService.StopAllContainersAsync(DefaultDrainTimeout, cancellationToken).ConfigureAwait(false);
        return new HostAgentLinuxAdminActionResult(_desiredState, report.Diagnostics);
    }

    public async Task<HostAgentLinuxAdminActionResult> StartupAsync(CancellationToken cancellationToken)
    {
        _desiredState = "running";
        var report = await _containerLifecycleService.StartAllContainersAsync(cancellationToken).ConfigureAwait(false);
        return new HostAgentLinuxAdminActionResult(_desiredState, report.Diagnostics);
    }

    public async Task<HostAgentLinuxAdminActionResult> RestartAsync(CancellationToken cancellationToken)
    {
        var shutdown = await ShutdownAsync(cancellationToken).ConfigureAwait(false);
        var startup = await StartupAsync(cancellationToken).ConfigureAwait(false);
        return new HostAgentLinuxAdminActionResult(_desiredState, [.. shutdown.Diagnostics, .. startup.Diagnostics]);
    }

    public async Task<IReadOnlyList<string>> ReadLogsAsync(int tail, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var lines = status.Diagnostics.Count == 0 ? ["HostAgent.Linux is running."] : status.Diagnostics;
        return lines.TakeLast(Math.Clamp(tail, 1, 5_000)).ToArray();
    }

    private static string ResolveWebUiUrl()
    {
        return Environment.GetEnvironmentVariable("XE_NODE_WEB_UI_URL") ?? string.Empty;
    }

    private static string ResolveComponentHealth(IReadOnlyList<RuntimeComponentStatusDto> components,
        params string[] names)
    {
        var component = components.FirstOrDefault(component =>
            names.Any(name => string.Equals(component.Name, name, StringComparison.OrdinalIgnoreCase)));

        return component?.Health switch
        {
            null => "unknown",
            ContainerHealth.Starting => "starting",
            ContainerHealth.Healthy => "healthy",
            ContainerHealth.Unhealthy => "unhealthy",
            ContainerHealth.Stopped => "stopped",
            _ => "unknown"
        };
    }
}

/// <summary>
///     Value object carrying host agent linux admin status data.
/// </summary>
public sealed record HostAgentLinuxAdminStatus(
    string State,
    string DesiredState,
    string WebUiUrl,
    string Ollama,
    string WebServer,
    DateTimeOffset StartedAt,
    bool BootstrapModelReady,
    IReadOnlyList<string> Diagnostics);

/// <summary>
///     Value object carrying host agent linux admin action result data.
/// </summary>
public sealed record HostAgentLinuxAdminActionResult(string DesiredState, IReadOnlyList<string> Diagnostics);
