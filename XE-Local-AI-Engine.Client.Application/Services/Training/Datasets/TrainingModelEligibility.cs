namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The training pipeline's model allow-list rule: an external OpenAI-compatible model is never a teacher, a critic
///     or a judge, whatever its declared locality.
/// </summary>
/// <remarks>
///     <para>
///         The existing refusal covers CLOUD models only, and a declared-LOCAL external model passes it: the capability
///         resolver reports it node-local, which is correct for the tool and knowledge gates and wrong here. Training
///         needs more than "the prompt stays inside the trust boundary" — it needs a runtime the node owns. A dataset
///         is generated over hours against a pinned definition, and an endpoint the node neither launched nor versioned
///         can change model, quantization or sampling underneath the run with nothing recording that it did, leaving a
///         dataset whose provenance claims a determinism it never had.
///     </para>
///     <para>
///         Enforced by MODEL ID rather than by resolved provider name so it holds even when the provider map has not
///         been reconciled yet: the <c>ext:</c> scheme is the identity, and an id carrying it is refused before any
///         lookup that could fail open.
///     </para>
/// </remarks>
internal static class TrainingModelEligibility
{
    /// <summary>
    ///     Throws when <paramref name="modelName" /> is an external model.
    /// </summary>
    /// <param name="modelName">The candidate model id.</param>
    /// <param name="roleDescription">The plural role, for the message ("dataset generation teachers").</param>
    /// <exception cref="TrainingValidationException">The model is external.</exception>
    public static void EnsureNotExternal(string? modelName, string roleDescription)
    {
        if (ExternalModelId.HasExternalScheme(modelName))
        {
            throw new TrainingValidationException(
                $"'{modelName}' is an external model; {roleDescription} must run on a runtime this node owns.");
        }
    }
}
