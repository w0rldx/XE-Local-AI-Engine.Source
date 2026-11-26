namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.Training.Runtime;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>A gate-free CPU job's hold on the training venv: registered as a runtime user, then the runtime rechecked. Dispose once its script run returns.</summary>
/// <remarks>
///     The registration comes first: an install or remove that published its phase earlier reads as not Ready here, and one that
///     publishes later refuses on <see cref="TrainingRuntimeUsers.InUse" />. A refused or throwing entry gives the registration back at once.
/// </remarks>
internal sealed class ModelLabRuntimeJob : IDisposable
{
    public const string RuntimeChangingMessage = "The training runtime is being changed. Try again in a moment.";

    private readonly IDisposable _registration;

    private ModelLabRuntimeJob(IDisposable registration, string interpreter, string scriptPath)
    {
        _registration = registration;
        Interpreter = interpreter;
        ScriptPath = scriptPath;
    }

    public string Interpreter { get; }

    public string ScriptPath { get; }

    /// <param name="notInstalledMessage">The caller's refusal when the runtime is at rest and not usable; a runtime mid-change gets <see cref="RuntimeChangingMessage" />.</param>
    public static bool TryEnter(TrainingRuntimeUsers users,
        ITrainingRuntimeService runtime,
        string scriptPath,
        string notInstalledMessage,
        [NotNullWhen(true)] out ModelLabRuntimeJob? job,
        [NotNullWhen(false)] out string? refusalMessage)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(runtime);
        var registration = users.Enter();
        try
        {
            var status = runtime.GetStatus();
            if (status.Phase == TrainingRuntimePhase.Ready && runtime.ResolveInterpreterPath() is { } interpreter && File.Exists(scriptPath))
            {
                job = new ModelLabRuntimeJob(registration, interpreter, scriptPath);
                registration = null;
                refusalMessage = null;
                return true;
            }

            job = null;
            refusalMessage = status.Terminal ? notInstalledMessage : RuntimeChangingMessage;
            return false;
        }
        finally
        {
            // Null once the job owns it; otherwise a refusal or a throwing recheck gives it back.
            registration?.Dispose();
        }
    }

    public void Dispose() =>
        _registration.Dispose();
}
