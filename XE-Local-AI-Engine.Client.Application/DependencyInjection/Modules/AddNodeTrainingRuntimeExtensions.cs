namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.Training;

/// <summary>
///     Registers the Python training runtime (uv/venv mechanics) and base-checkpoint acquisition.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> invoke after <c>AddNodeModelRuntime</c> — the base-checkpoint store rides the
///     shared Hugging Face download and hub clients that <c>AddHuggingFaceGgufStore</c> registers there, exactly as the
///     image-model store does.
/// </remarks>
internal static class AddNodeTrainingRuntimeExtensions
{
    public static IHostApplicationBuilder AddNodeTrainingRuntime(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Services.AddTrainingRuntime();
        _ = builder.Services.AddHuggingFaceBaseCheckpointStore();

        builder.Services.AddScoped<ITrainingBaseArtifactStore, TrainingBaseArtifactStore>();
        builder.Services.AddScoped<ITrainingRunStore, TrainingRunStore>();
        builder.Services.AddSingleton<BaseArtifactDownloadCoordinator>();
        builder.Services.AddScoped<IBaseArtifactService, BaseArtifactService>();
        return builder;
    }
}
