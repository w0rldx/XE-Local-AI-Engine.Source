namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>How long a caller expects the model it uses to stay loaded afterwards, and whether a user waits on it; a hint providers may ignore.</summary>
public enum ModelResidencyIntent
{
    /// <summary>Normal use: a model this request loads keeps the provider's normal idle lifetime.</summary>
    Interactive = 0,

    /// <summary>
    ///     A one-off request, such as an AI Assist draft: a model loaded only for it gets a short idle lifetime. A model
    ///     that was already loaded keeps its normal lifetime.
    /// </summary>
    Transient = 1,

    /// <summary>
    ///     Work no user waits on, such as memory extraction: the <see cref="Interactive" /> lifetime, but a cold load for
    ///     it never unloads or waits for another chat model, for memory or at the process cap; it loads only if it fits.
    /// </summary>
    Background = 2
}
