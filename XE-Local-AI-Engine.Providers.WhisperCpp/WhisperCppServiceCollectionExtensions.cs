namespace XE_Local_AI_Engine.Providers.WhisperCpp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     DI wiring for the whole whisper.cpp runtime: binary acquisition, backend selection, the bring-your-own
///     override, process supervision, and the transcriber the engine calls.
/// </summary>
/// <remarks>
///     <para>
///         One extension rather than the image runtime's provider/runtime pair: there is a single consumer and no
///         split worth expressing. Every registration is <c>TryAdd</c>, so a host may override any seam — and so the
///         composition root's own seeded <see cref="WhisperRuntimeOptions" />, registered BEFORE this call, wins over
///         the default here.
///     </para>
///     <para>
///         <strong>Caller contract:</strong> the consuming application must register an <see cref="IHardwareProfiler" />
///         before resolving the backend selector.
///     </para>
/// </remarks>
public static class WhisperCppServiceCollectionExtensions
{
    /// <summary>Named HTTP client for pinned prebuilt-binary downloads.</summary>
    public const string BinaryHttpClientName = "whispercpp-binary";

    /// <summary>Named HTTP client for loopback health, model-load and transcription requests.</summary>
    public const string RuntimeHttpClientName = "whispercpp-runtime";

    /// <summary>Registers the whisper.cpp runtime infrastructure.</summary>
    public static IServiceCollection AddWhisperCppRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Built once from the operator-trust env channel, never from IConfiguration or a DTO.
        services.TryAddSingleton(WhisperServerRuntimeOverrideOptions.FromEnvironment());

        services.TryAddSingleton(new WhisperRuntimeOptions());
        services.TryAddSingleton<IWhisperRuntimeActivityGate, WhisperRuntimeActivityGate>();
        services.TryAddSingleton<IWhisperInstalledRuntimeStore, WhisperInstalledRuntimeStore>();
        services.TryAddSingleton<IWhisperManagedSourceBuildSignal, WhisperManagedSourceBuildSignal>();

        services.AddHttpClient(BinaryHttpClientName);

        // The runtime client carries NO resilience pipeline and NO client-level timeout, and both halves matter.
        //
        // Aspire's AddServiceDefaults installs a standard resilience handler on EVERY named client through
        // ConfigureHttpClientDefaults, including one registered later, and that pipeline's per-attempt timeout is ten
        // seconds — which would abort a CPU transcription that legitimately runs for minutes, while the status DTO
        // advertises a thirty-minute budget. It also retries every method by default, and neither the model load nor
        // the transcription is idempotent. RemoveAllResilienceHandlers strips it, and is a no-op outside Aspire.
        //
        // A single HttpClient.Timeout cannot serve three requests whose right budgets differ by four orders of
        // magnitude, and its 100-second default would abort a long transcription on its own. Infinite here is safe
        // ONLY because every call site owns an explicit deadline through a linked token source; nothing may call this
        // client without one.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; used deliberately to drop the
        // Aspire-installed standard pipeline, whose attempt timeout and blanket retries are
        // both wrong for a local transcription daemon.
        services.AddHttpClient(RuntimeHttpClientName, static client => client.Timeout = Timeout.InfiniteTimeSpan)
                .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.TryAddSingleton<IWhisperBackendSelector>(static sp =>
            new WhisperBackendSelector(sp.GetRequiredService<IHardwareProfiler>(),
                sp.GetRequiredService<WhisperServerRuntimeOverrideOptions>(),
                sp.GetRequiredService<IWhisperManagedSourceBuildSignal>()));

        services.TryAddSingleton<IWhisperCppBinaryManager>(static sp =>
            new WhisperCppBinaryManager(sp.GetRequiredService<IHttpClientFactory>().CreateClient(BinaryHttpClientName),
                cacheRoot: null,
                activeTag: null,
                sp.GetRequiredService<WhisperServerRuntimeOverrideOptions>(),
                sp.GetRequiredService<IWhisperInstalledRuntimeStore>(),
                sp.GetRequiredService<IWhisperManagedSourceBuildSignal>()));

        services.TryAddSingleton<IWhisperServerProcessLauncher, WhisperServerProcessLauncher>();
        services.TryAddSingleton<IWhisperServerReadinessProbe>(static sp =>
            new WhisperServerReadinessProbe(sp.GetRequiredService<IHttpClientFactory>().CreateClient(RuntimeHttpClientName),
                sp.GetRequiredService<WhisperRuntimeOptions>()));

        // GPU-load admission floor, so a provider-only host resolves the gate. The composition root overrides it with
        // a plain AddSingleton carrying the real singleton shared with the llama-server and image supervisors.
        services.TryAddSingleton<IGpuModelLoadAdmission, NoOpGpuModelLoadAdmission>();

        // Strictly one supervisor: it owns the node's only whisper-server child process. Built through a factory
        // because its constructor takes the internal launcher and readiness seams.
        services.TryAddSingleton(static sp => new WhisperServerProcessSupervisor(sp.GetRequiredService<IWhisperBackendSelector>(),
            sp.GetRequiredService<IWhisperCppBinaryManager>(),
            sp.GetRequiredService<IWhisperServerProcessLauncher>(),
            sp.GetRequiredService<IWhisperServerReadinessProbe>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(RuntimeHttpClientName),
            sp.GetRequiredService<WhisperRuntimeOptions>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WhisperServerProcessSupervisor>>(),
            sp.GetRequiredService<IGpuModelLoadAdmission>(),
            sp.GetRequiredService<IWhisperRuntimeActivityGate>()));
        services.TryAddSingleton<IWhisperServerSupervisor>(static sp => sp.GetRequiredService<WhisperServerProcessSupervisor>());

        services.TryAddSingleton<IWhisperTranscriber>(static sp =>
            new WhisperServerTranscriber(sp.GetRequiredService<IWhisperServerSupervisor>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(RuntimeHttpClientName),
                sp.GetRequiredService<WhisperRuntimeOptions>(),
                sp.GetRequiredService<ILogger<WhisperServerTranscriber>>()));

        // Startup orphan reaper: a hard kill of the host skips the supervisor's graceful teardown, leaving a daemon
        // holding its loopback port and its memory. It matches ONLY binaries under our own cache root.
        services.TryAddSingleton<IStaleWhisperServerProcessScanner, OsStaleWhisperServerProcessScanner>();
        services.AddHostedService(static sp => new StaleWhisperServerReaper(sp.GetRequiredService<IStaleWhisperServerProcessScanner>(),
            WhisperCppBinaryManager.DefaultWhisperBinariesRoot(),
            sp.GetRequiredService<ILogger<StaleWhisperServerReaper>>()));

        // The managed Linux CUDA source-build lane. There is no prebuilt Linux CUDA asset upstream, so this is the
        // only way a Linux node gets GPU transcription without a bring-your-own binary.
        services.TryAddSingleton<IWhisperCppSourceBuildPrerequisiteProbe, WhisperCppSourceBuildPrerequisiteProbe>();
        services.TryAddSingleton<IWhisperCppSourceBuildEventPublisher, NullWhisperCppSourceBuildEventPublisher>();
        services.TryAddSingleton<IWhisperCppSourceBuildService, WhisperCppSourceBuildService>();

        // The hosted service is not optional bookkeeping: it republishes the managed-runtime signal at start, and the
        // backend selector trusts a managed CUDA build only once that signal is set. Without it an adopted build
        // stops resolving after a restart and the node silently falls back to CPU.
        services.AddHostedService<WhisperCppSourceBuildLifecycle>();

        return services;
    }
}
