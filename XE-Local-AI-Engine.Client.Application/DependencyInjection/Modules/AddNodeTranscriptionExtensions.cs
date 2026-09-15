namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
using XE_Local_AI_Engine.Client.Services.Transcription.Live;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Wires local audio transcription: the whisper.cpp runtime, the weight store, the download coordinator and the
///     runtime facade the endpoints call. Must run AFTER <c>AddNodeModelRuntime</c>, for the same reason
///     <c>AddNodeImages</c> does — the weight store reuses the Hugging Face download client that
///     <c>AddHuggingFaceGgufStore</c> registers.
/// </summary>
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

        // Seeded BEFORE AddWhisperCppRuntime, so the provider's TryAddSingleton default is a no-op. Both paths come
        // from the application layer because the provider must not resolve a node data directory itself, and the idle
        // TTL is an operator setting rather than a provider constant.
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

        // Live sessions. Singleton, and deliberately NOT an IHostedService: the end-to-end test factory removes every
        // hosted service, so a background-timer design would be dead there. Everything is driven by pushed frames plus
        // timers created from the injected TimeProvider.
        builder.Services.AddSingleton<ILiveTranscriptionSessionRegistry, LiveTranscriptionSessionRegistry>();

        // The transcription service. Singleton: the in-flight cancellation registry must outlive the request that
        // started a transcription, and it composes the singleton whisper runtime; it opens its own scope per store
        // operation. It takes the registry so cancel and delete route a live session through one termination path;
        // the registry resolves this service lazily, so the two singletons do not close a constructor cycle.
        builder.Services.AddSingleton<ITranscriptionService, TranscriptionService>();

        return builder;
    }
}
