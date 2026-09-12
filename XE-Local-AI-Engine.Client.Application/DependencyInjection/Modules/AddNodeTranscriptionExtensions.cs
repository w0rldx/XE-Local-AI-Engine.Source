namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
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

        return builder;
    }
}
