namespace XE_Local_AI_Engine.Client.Services.Invocation;

using XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     Fixed, path-free failure sentences shared by the invocation failure classifier and the operator-facing
///     invocation monitor, which shows one per <see cref="FailureCategory" /> instead of the stored error.
/// </summary>
public static class InvocationFailureMessages
{
    /// <summary>
    ///     The monitor sentence for <see cref="FailureCategory.ModelUnavailable" />, which covers a model missing on the
    ///     node and one the provider does not offer.
    /// </summary>
    public const string ModelUnavailable = "The selected model is not installed or not reachable. Install it or choose another model.";

    /// <summary>The sentence for <see cref="FailureCategory.ModelLoadFailed" />.</summary>
    public const string ModelLoadFailed = "The model could not be loaded or run on the provider.";

    /// <summary>The sentence for a generic <see cref="FailureCategory.Timeout" />.</summary>
    public const string TimedOut = "The operation timed out.";
}
