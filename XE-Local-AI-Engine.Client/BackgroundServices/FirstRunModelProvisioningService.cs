namespace XE_Local_AI_Engine.Client.BackgroundServices;

using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     First-run provisioning for the self-contained desktop launch: ensures a small node-local GGUF chat model is
///     installed (via the bundled llama.cpp runtime) and selected, so a fresh double-click install can chat without the
///     operator first downloading a model or running Ollama.
/// </summary>
/// <remarks>
///     <para>
///         <b>Desktop-gated.</b> The whole flow runs only when the process was launched in desktop mode
///         (<c>XE_LAUNCH_MODE=desktop</c> / <c>--desktop</c>). Headless, Aspire, and CI runs are byte-behavior-unchanged
///         — they never auto-download a model.
///     </para>
///     <para>
///         <b>Non-blocking + offline-tolerant.</b> All work runs in <see cref="ExecuteAsync" /> off the startup path; a
///         multi-GB binary/model download never blocks the host from coming up. Any transport failure (HF unreachable,
///         binary acquisition failure) is caught and logged, leaving the empty-picker onboarding as the fallback — the
///         service never crashes startup.
///     </para>
///     <para>
///         <b>Idempotent.</b> It no-ops when a GGUF is already installed or a non-default <c>DefaultModelName</c> is set,
///         so it provisions at most once and is safe to run on every boot.
///     </para>
/// </remarks>
public sealed class FirstRunModelProvisioningService : BackgroundService
{
    // Whole-probe ceiling: the single wall-clock bound on GPU-variant detection. The probe itself enforces a shorter
    // per-tool timeout (ProcessGpuVendorProbe.ProbeTimeout, 8s) and reaps its child; this ceiling covers the rare case
    // where the fast path + both shelling probes chain. Generous so a slow-but-working detection still succeeds.
    private static readonly TimeSpan DefaultGpuProbeCeiling = TimeSpan.FromSeconds(25);

    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly IConfiguration _configuration;
    private readonly IGgufDownloadCoordinator _downloadCoordinator;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly TimeSpan _gpuProbeCeiling;
    private readonly bool _isDesktopMode;
    private readonly ILogger<FirstRunModelProvisioningService> _logger;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly TimeSpan _pollInterval;
    private readonly IGpuVariantSelector _variantSelector;

    public FirstRunModelProvisioningService(IConfiguration configuration,
        IGgufModelStore ggufModelStore,
        IGgufDownloadCoordinator downloadCoordinator,
        ILlamaCppBinaryManager binaryManager,
        IGpuVariantSelector variantSelector,
        INodeSettingsStore nodeSettingsStore,
        ILogger<FirstRunModelProvisioningService> logger)
        : this(configuration,
            ggufModelStore,
            downloadCoordinator,
            binaryManager,
            variantSelector,
            nodeSettingsStore,
            logger,
            DesktopLaunch.IsDesktopMode(Environment.GetCommandLineArgs(), VelopackInstall.IsManaged()),
            TimeSpan.FromSeconds(2),
            DefaultGpuProbeCeiling)
    {
    }

    // Test seam: injects the desktop-mode decision, the download-poll interval, and the GPU-probe ceiling so the
    // provisioning sequence (including the probe-overrun fallback) is exercisable without mutating real process
    // args/env, without a 2s/tick wait, and without a 25s ceiling wait. Mirrors DesktopLaunch's injectable-reader
    // pattern. Production uses the public ctor, which resolves the real desktop decision.
    internal FirstRunModelProvisioningService(IConfiguration configuration,
        IGgufModelStore ggufModelStore,
        IGgufDownloadCoordinator downloadCoordinator,
        ILlamaCppBinaryManager binaryManager,
        IGpuVariantSelector variantSelector,
        INodeSettingsStore nodeSettingsStore,
        ILogger<FirstRunModelProvisioningService> logger,
        bool isDesktopMode,
        TimeSpan pollInterval,
        TimeSpan gpuProbeCeiling)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isDesktopMode = isDesktopMode;
        _pollInterval = pollInterval;
        _gpuProbeCeiling = gpuProbeCeiling;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Desktop-only: headless / Aspire / CI must never auto-download a model (off-flag invariant).
        if (!_isDesktopMode)
        {
            return;
        }

        if (!_configuration.GetValue("FirstRunModel:Enabled", defaultValue: true))
        {
            return;
        }

        // Visible entry marker so an operator log shows the service ran (and reached desktop mode) even when a later
        // phase stalls. The desktop gate above stays silent to preserve the headless/CI off-flag invariant.
        _logger.LogInformation("First-run model provisioning starting (desktop mode).");

        try
        {
            await ProvisionAsync(stoppingToken).ConfigureAwait(false);
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
        var installed = await _ggufModelStore.ListInstalledModelsAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("First-run provisioning: {Count} GGUF model(s) already installed.", installed.Count);
        if (installed.Count > 0)
        {
            return;
        }

        var settings = await _nodeSettingsStore.LoadAsync(ct).ConfigureAwait(false);
        var configuredDefault = _configuration.GetValue<string>("Agent:LocalChat:DefaultModel");
        if (!string.IsNullOrWhiteSpace(settings.DefaultModelName)
            && !string.Equals(settings.DefaultModelName, configuredDefault, StringComparison.OrdinalIgnoreCase))
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

        // Ensure the llama.cpp binary for this host BEFORE downloading the model so the model is immediately runnable.
        // A binary acquisition failure surfaces a sanitized LlamaRuntimeException that the caller's catch turns into the
        // empty-picker fallback.
        //
        // GPU-variant detection prefers a non-shelling NVML driver-presence signal and only shells out to vendor tools
        // (nvidia-smi / wmic) as a fallback; those can hang on some Windows hosts. ONE timeout governs the whole
        // selection: a linked CancellationTokenSource with a hard ceiling. The probe is cancellation-linked and reaps
        // any child process it spawned in a finally block, so cancelling it here can NEVER leave an orphaned process —
        // there is no second wall-clock race and no abandoned probe task. On timeout/failure we fall back to the CPU
        // runtime, which always works (just slower) — provisioning reaching the download is the priority.
        _logger.LogInformation("First-run provisioning detecting the GPU runtime variant.");
        GpuVariant variant;
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(_gpuProbeCeiling);
        try
        {
            variant = await _variantSelector.SelectVariantAsync(probeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The probe overran the ceiling (a wedged vendor tool); the probe's own finally reaped its child. Fall back
            // to the CPU runtime so a stuck probe never blocks first-run provisioning beyond the ceiling.
            _logger.LogWarning("First-run provisioning: GPU runtime detection did not complete within {Ceiling}; falling back to the CPU runtime.", _gpuProbeCeiling);
            variant = GpuVariant.Cpu;
        }

        _logger.LogInformation(
            "First-run provisioning acquiring the llama.cpp runtime ({Variant}) for first-run model '{RepoId}' — this downloads the runtime on first run and can take a few minutes.", variant,
            repoId.Trim());
        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);
        _logger.LogInformation("First-run provisioning ensured the llama.cpp runtime ({Variant}, version {Version}).", variant, binary.Version);

        // Download the default GGUF through the coordinator's detached path so progress/cancel AND the llamacpp
        // model_provider_map write happen through the SAME code as an operator-initiated download (FRR-2). The ticket
        // carries the canonical {repo:quant} identity the model is installed under.
        var request = new GgufModelRequest
        {
            RepoId = repoId.Trim(),
            Quant = string.IsNullOrWhiteSpace(quant) ? null : quant.Trim(),
            Role = GgufRole.Chat
        };
        _logger.LogInformation("First-run provisioning starting model download '{RepoId}' (quant {Quant}).", request.RepoId, request.Quant ?? "(none)");
        var ticket = await _downloadCoordinator.StartAsync(request, ct).ConfigureAwait(false);
        _logger.LogInformation("First-run provisioning download started for '{Model}'; waiting for completion.", ticket.ModelName);

        // The download runs detached; wait for it to reach a terminal phase so DefaultModelName is set only once the
        // file is actually present (a half-downloaded model must not be selected).
        var completed = await WaitForDownloadAsync(ticket.ModelName, ct).ConfigureAwait(false);
        if (!completed)
        {
            _logger.LogWarning("First-run model '{Model}' did not finish downloading; leaving the picker empty for onboarding.", ticket.ModelName);
            return;
        }

        // Select the freshly-installed GGUF as the node default so the chat composer opens on a ready model.
        var updated = settings with
        {
            DefaultModelName = ticket.ModelName
        };
        await _nodeSettingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
        _logger.LogInformation("First-run provisioning installed and selected '{Model}'.", ticket.ModelName);
    }

    /// <summary>
    ///     Polls the download coordinator's sanitized status until the named download reaches a terminal phase. Returns
    ///     <see langword="true" /> only when it completed (the file is present); <see langword="false" /> on cancel or
    ///     failure. Polls rather than blocks because the coordinator runs the download detached and exposes progress via
    ///     a status registry.
    /// </summary>
    private async Task<bool> WaitForDownloadAsync(string modelName, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
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
