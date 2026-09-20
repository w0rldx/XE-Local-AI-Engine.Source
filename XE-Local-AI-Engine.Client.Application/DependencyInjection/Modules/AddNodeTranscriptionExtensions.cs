namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
using XE_Local_AI_Engine.Client.Services.Transcription.Live;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Wires local audio transcription: the whisper.cpp runtime, the weight store, the download coordinator and the
///     runtime facade the endpoints call.
/// </summary>
/// <remarks>
///     Must run AFTER <c>AddNodeModelRuntime</c>, for the same reason <c>AddNodeImages</c> does: the weight store
///     reuses the Hugging Face download client that <c>AddHuggingFaceGgufStore</c> registers.
/// </remarks>
internal static class AddNodeTranscriptionExtensions
{
    public static IHostApplicationBuilder AddNodeTranscription(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // No IValidateOptions<> class: unlike the dev-workflow options there is no cross-section invariant to check,
        // and the range annotation is the whole check.
        builder.Services.AddOptions<TranscriptionOptions>()
               .Bind(configuration.GetSection(TranscriptionOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        builder.Services.AddSingleton<WhisperModelPathResolver>();
        builder.Services.AddHuggingFaceWhisperWeightStore();

        // Singleton: a download outlives the request that started it, and its status registry is the only thing that
        // makes a failure observable at all.
        builder.Services.AddSingleton<IWhisperModelDownloadCoordinator, WhisperModelDownloadCoordinator>();

        // Seeded BEFORE AddWhisperCppRuntime, so the provider's TryAddSingleton default is a no-op. Both paths come from the
        // application layer, because the provider must not resolve a node data directory and the idle TTL is an operator setting.
        builder.Services.AddSingleton(sp =>
        {
            var pathResolver = sp.GetRequiredService<WhisperModelPathResolver>();
            return new WhisperRuntimeOptions
            {
                IdleTimeToLive = sp.GetRequiredService<INodeRuntimeSettings>().GetTranscriptionIdleTimeout(),
                VadModelPath = pathResolver.VadFilePath,
                ModelsDirectory = pathResolver.ModelsDirectory
            };
        });
        builder.Services.AddWhisperCppRuntime();

        // The six runtime and source-build endpoints' only path to the provider. Singleton, matching the four it wraps: the activity
        // gate, backend selector, source-build prerequisite probe and source-build service WhisperCppServiceCollectionExtensions TryAdds.
        builder.Services.AddSingleton<WhisperRuntimeOrchestrationService>();

        builder.Services.AddSingleton<ITranscriptionRuntimeService, TranscriptionRuntimeService>();

        // Persistence boundary for the session registry. Scoped: one NodeChatDbContext per operation (title, config
        // and segment text are encrypted at rest by the node encryption interceptor on save).
        builder.Services.AddScoped<ITranscriptionSessionStore, TranscriptionSessionStore>();

        // Engine-side ogg/m4a/webm to 16 kHz mono WAV conversion. Singleton: availability probes PATH for ffmpeg once
        // at construction, and that same answer is what the runtime status publishes as its transcode capability.
        builder.Services.AddSingleton<IAudioTranscoder, FfmpegAudioTranscoder>();

        // The no-op floor, so this layer resolves without a host. The host registers a SignalR-backed publisher after
        // it in the same collection and the later registration wins — the idiom the image and graph publishers use.
        builder.Services.TryAddSingleton<ITranscriptionEventPublisher, NullTranscriptionEventPublisher>();

        // Live sessions. Singleton, and deliberately NOT an IHostedService: the end-to-end test factory removes every hosted service,
        // so a background-timer design would be dead there. Pushed frames and timers from the injected TimeProvider drive everything.
        builder.Services.AddSingleton<ILiveTranscriptionSessionRegistry, LiveTranscriptionSessionRegistry>();

        // Per-application audio capture: WASAPI process loopback is a Windows mechanism with no PipeWire or PulseAudio equivalent, so
        // elsewhere the NotSupported source fails closed by name. This branch is what makes [SupportedOSPlatform] honest — never a CA1416 suppression.
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<IProcessAudioCaptureSource, WindowsProcessAudioCaptureSource>();
        }
        else
        {
            builder.Services.AddSingleton<IProcessAudioCaptureSource, NotSupportedProcessAudioCaptureSource>();
        }

        // Singleton, and registered on every OS: a capture outlives the request that started it, so a cancelled
        // HTTP request must not kill it. On a host without process loopback it simply never starts one.
        builder.Services.AddSingleton<ProcessAudioCaptureCoordinator>();

        // The transcription service. Singleton: the in-flight cancellation registry outlives the request that started a transcription,
        // and it opens its own scope per store operation. It takes the registry, which resolves it lazily, so the two close no ctor cycle.
        builder.Services.AddSingleton<ITranscriptionService, TranscriptionService>();

        return builder;
    }
}
