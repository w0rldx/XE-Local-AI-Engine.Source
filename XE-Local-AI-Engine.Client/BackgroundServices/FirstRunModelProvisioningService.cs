namespace XE_Local_AI_Engine.Client.BackgroundServices;

using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     First-run provisioning for the packaged desktop launch: ensures a small node-local GGUF chat model is installed
///     through the bundled llama.cpp runtime and selected, so a fresh double-click install can chat right away.
/// </summary>
/// <remarks>
///     Desktop-gated, non-blocking, offline-tolerant and idempotent; the three llama.cpp contracts it needs arrive
///     through <see cref="LlamaCppRuntimeOrchestrationService" />, the one door a host type may take. It owns the
///     terminal <see cref="RuntimeAcquisitionPhase.Failed" /> for the GPU-probe segment ALONE — never for the whole
///     flow. See docs/wiki/11-hosting-and-deployment.md ("First-run model provisioning (desktop)").
/// </remarks>
public sealed class FirstRunModelProvisioningService : BackgroundService
{
    // Whole-probe ceiling: the single wall-clock bound on GPU-variant detection. The probe enforces a shorter per-tool timeout
    // (ProcessGpuVendorProbe.ProbeTimeout, 8s) and reaps its child; this covers the fast path chaining both shelling probes.
    private static readonly TimeSpan DefaultGpuProbeCeiling = TimeSpan.FromSeconds(25);

    private readonly IConfiguration _configuration;
    private readonly IGgufDownloadCoordinator _downloadCoordinator;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly TimeSpan _gpuProbeCeiling;
    private readonly bool _isLocalMode;
    private readonly ILogger<FirstRunModelProvisioningService> _logger;
    private readonly INodeRuntimeSettings _nodeRuntimeSettings;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly TimeSpan _pollInterval;
    private readonly LlamaCppRuntimeOrchestrationService _runtime;
    private readonly TimeProvider _timeProvider;

    public FirstRunModelProvisioningService(IConfiguration configuration,
        IGgufModelStore ggufModelStore,
        IGgufDownloadCoordinator downloadCoordinator,
        LlamaCppRuntimeOrchestrationService runtime,
        INodeSettingsStore nodeSettingsStore,
        INodeRuntimeSettings nodeRuntimeSettings,
        TimeProvider timeProvider,
        ILogger<FirstRunModelProvisioningService> logger)
        : this(configuration,
            ggufModelStore,
            downloadCoordinator,
            runtime,
            nodeSettingsStore,
            nodeRuntimeSettings,
            timeProvider,
            logger,
            DesktopLaunch.ResolveLaunchMode(Environment.GetCommandLineArgs(), VelopackInstall.IsManaged()).IsLocalMode(),
            TimeSpan.FromSeconds(2),
            DefaultGpuProbeCeiling)
    {
    }

    // Test seam injecting the desktop-mode decision, the download-poll interval and the GPU-probe ceiling, so the sequence
    // (probe overrun included) runs without touching real args/env and without the tick or ceiling waits. Production uses the public ctor.
    internal FirstRunModelProvisioningService(IConfiguration configuration,
        IGgufModelStore ggufModelStore,
        IGgufDownloadCoordinator downloadCoordinator,
        LlamaCppRuntimeOrchestrationService runtime,
        INodeSettingsStore nodeSettingsStore,
        INodeRuntimeSettings nodeRuntimeSettings,
        TimeProvider timeProvider,
        ILogger<FirstRunModelProvisioningService> logger,
        bool isLocalMode,
        TimeSpan pollInterval,
        TimeSpan gpuProbeCeiling)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isLocalMode = isLocalMode;
        _pollInterval = pollInterval;
        _gpuProbeCeiling = gpuProbeCeiling;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Local-mode-only: headless / Aspire / CI must never auto-download a model (off-mode invariant).
        if (!_isLocalMode)
        {
            return;
        }

        if (!_configuration.GetValue("FirstRunModel:Enabled", defaultValue: true))
        {
            return;
        }

        try
        {
            // After the existing gates and before the entry marker and ProvisionAsync, so a disabled node starts no
            // download and an undecided one waits for the operator's choice instead of deciding for them.
            await ExternalAccessGate.WaitUntilDecidedAsync(_nodeRuntimeSettings, _timeProvider, stoppingToken);
            if (!await _nodeRuntimeSettings.GetAutoProvisionFirstRunModelAsync(stoppingToken))
            {
                _logger.LogDebug("First-run model provisioning is disabled by the node's external-access settings.");
                return;
            }

            // Visible entry marker so an operator log shows the service ran (and reached desktop mode) even when a
            // later phase stalls. The desktop gate above stays silent to preserve the headless/CI off-flag invariant.
            _logger.LogInformation("First-run model provisioning starting (desktop mode).");

            await ProvisionAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — nothing to provision.
        }
        catch (Exception exception)
        {
            // Offline-tolerant: never crash startup. The empty-picker onboarding remains the fallback.
            _logger.LogWarning(exception, "First-run model provisioning failed; the operator can install a model manually.");
        }
    }

    private async Task ProvisionAsync(CancellationToken ct)
    {
        // Idempotency: if any GGUF is already installed, or a non-default model is already selected, there is nothing to
        // provision. This makes the service safe to run on every boot.
        var installed = await _ggufModelStore.ListInstalledModelsAsync(ct);
        _logger.LogInformation("First-run provisioning: {Count} GGUF model(s) already installed.", installed.Count);
        if (installed.Count > 0)
        {
            return;
        }

        var settings = await _nodeSettingsStore.LoadAsync(ct);
        var configuredDefault = _configuration.GetValue<string>("Agent:LocalChat:DefaultModel");
        if (!MayAutoSelectDefaultModel(settings.DefaultModelName, configuredDefault))
        {
            _logger.LogInformation("First-run provisioning skipped: a non-default model '{Model}' is already selected.", settings.DefaultModelName);
            return;
        }

        var repoId = _configuration.GetValue<string>("FirstRunModel:RepoId");
        if (string.IsNullOrWhiteSpace(repoId))
        {
            _logger.LogInformation("First-run provisioning skipped: no FirstRunModel:RepoId is configured.");
            return;
        }

        var quant = _configuration.GetValue<string>("FirstRunModel:Quant");

        // Ensure the llama.cpp binary BEFORE downloading the model, so the model is immediately runnable; an acquisition failure surfaces a
        // sanitized LlamaRuntimeException the caller's catch turns into the empty-picker fallback. ONE linked CancellationTokenSource governs the whole GPU-variant selection.
        _logger.LogInformation("First-run provisioning detecting the GPU runtime variant.");
        GpuVariant variant;
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(_gpuProbeCeiling);
        try
        {
            // The acquisition channel opens HERE, not at the top of ProvisionAsync: this probe is the first silent multi-second
            // phase an operator sees no explanation for. Reporting is fire-and-forget inside the registry, so it adds no await.
            _runtime.ReportRuntimeAcquisition(new RuntimeAcquisitionUpdate
            {
                Phase = RuntimeAcquisitionPhase.DetectingGpu
            });
            variant = await _runtime.SelectGpuVariantAsync(probeCts.Token);
        }
        catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The probe overran the ceiling (a wedged vendor tool) and its own finally reaped the child. The CPU runtime keeps
            // provisioning moving: a FALLBACK, not a failure, so publishing Failed here would banner a run that succeeds.
            _logger.LogWarning("First-run provisioning: GPU runtime detection did not complete within {Ceiling}; falling back to the CPU runtime.", _gpuProbeCeiling);
            variant = GpuVariant.Cpu;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Terminal state is scoped to the ONE segment this service owns; the manager owns Failed for download/verify/extract and the
            // outer ExecuteAsync catch must never publish it. Cancellation is excluded: a shutting-down host is not an acquisition failure.
            _runtime.ReportRuntimeAcquisition(new RuntimeAcquisitionUpdate
            {
                Phase = RuntimeAcquisitionPhase.Failed,
                SanitizedError = SanitizeAcquisitionFailure(exception)
            });

            // Propagate exactly as before, so the outer catch still swallows + logs and startup never crashes.
            throw;
        }

        _logger.LogInformation(
            "First-run provisioning acquiring the llama.cpp runtime ({Variant}) for first-run model '{RepoId}' — this downloads the runtime on first run and can take a few minutes.", variant,
            repoId.Trim());
        var binary = await _runtime.EnsureBinaryAsync(variant, ct);
        _logger.LogInformation("First-run provisioning ensured the llama.cpp runtime ({Variant}, version {Version}).", variant, binary.Version);

        // Download the default GGUF through the coordinator's detached path, so progress/cancel AND the llamacpp model_provider_map
        // write happen through the SAME code as an operator-initiated download (FRR-2), under the canonical {repo:quant} identity.
        var request = new GgufModelRequest
        {
            RepoId = repoId.Trim(),
            Quant = string.IsNullOrWhiteSpace(quant) ? null : quant.Trim(),
            Role = GgufRole.Chat
        };
        _logger.LogInformation("First-run provisioning starting model download '{RepoId}' (quant {Quant}).", request.RepoId, request.Quant ?? "(none)");
        var ticket = await _downloadCoordinator.StartAsync(request, ct);
        _logger.LogInformation("First-run provisioning download started for '{Model}'; waiting for completion.", ticket.ModelName);

        // The download runs detached; wait for it to reach a terminal phase so DefaultModelName is set only once the
        // file is actually present (a half-downloaded model must not be selected).
        var completed = await WaitForDownloadAsync(ticket.ModelName, ct);
        if (!completed)
        {
            _logger.LogWarning("First-run model '{Model}' did not finish downloading; leaving the picker empty for onboarding.", ticket.ModelName);
            return;
        }

        // Select the freshly-installed GGUF as the node default: a read-modify-write under the store's lock, with the skip precondition re-checked against the
        // WRITE-time record. Returning `latest` still writes, because the store has no no-change early return — one redundant write on a first run.
        string? operatorSelection = null;
        await _nodeSettingsStore.UpdateAsync(latest =>
        {
            if (!MayAutoSelectDefaultModel(latest.DefaultModelName, configuredDefault))
            {
                operatorSelection = latest.DefaultModelName;
                return latest;
            }

            return latest with
            {
                DefaultModelName = ticket.ModelName
            };
        }, ct);

        if (operatorSelection is not null)
        {
            _logger.LogInformation("First-run provisioning installed '{Model}' but kept the model '{Selected}' the operator selected while it downloaded.", ticket.ModelName,
                operatorSelection);
            return;
        }

        _logger.LogInformation("First-run provisioning installed and selected '{Model}'.", ticket.ModelName);
    }

    /// <summary>
    ///     Whether first-run provisioning owns <c>DefaultModelName</c>: nothing is selected yet, or what is selected is
    ///     the configured fallback this service exists to replace.
    /// </summary>
    /// <remarks>
    ///     Anything else is an operator's own pick and must survive. Shared by the pre-download skip and the
    ///     post-download write so the two cannot drift.
    /// </remarks>
    private static bool MayAutoSelectDefaultModel(string? selected, string? configuredDefault) =>
        string.IsNullOrWhiteSpace(selected)
        || string.Equals(selected, configuredDefault, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Reduces a GPU-probe failure to operator-safe text for the acquisition banner.
    /// </summary>
    /// <remarks>
    ///     <see cref="LlamaRuntimeException" /> messages are user-safe by contract, so they pass through; anything else
    ///     is collapsed to a generic reason rather than surfaced verbatim, since an arbitrary exception message can
    ///     carry an absolute path or a command line.
    /// </remarks>
    private static string SanitizeAcquisitionFailure(Exception exception)
    {
        return exception is LlamaRuntimeException runtimeException
            ? runtimeException.Message
            : "Could not detect the graphics runtime for this machine.";
    }

    /// <summary>
    ///     Polls the download coordinator's sanitized status until the named download reaches a terminal phase.
    /// </summary>
    /// <remarks>
    ///     Returns <see langword="true" /> only when it completed and the file is present, <see langword="false" /> on
    ///     cancel or failure. It polls rather than blocks because the coordinator runs the download detached and
    ///     exposes progress through a status registry.
    /// </remarks>
    private async Task<bool> WaitForDownloadAsync(string modelName, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            var status = _downloadCoordinator.GetStatus(modelName);
            switch (status?.Phase)
            {
                case GgufDownloadPhase.Completed:
                    return true;
                case GgufDownloadPhase.Failed:
                    _logger.LogWarning("First-run model '{Model}' download failed: {Reason}", modelName, status.SanitizedError ?? "unknown reason");
                    return false;
                case GgufDownloadPhase.Cancelled:
                    return false;
            }
        }

        return false;
    }
}
