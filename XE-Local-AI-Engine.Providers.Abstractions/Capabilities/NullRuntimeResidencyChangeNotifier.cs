namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>No-op <see cref="IRuntimeResidencyChangeNotifier" /> floor for provider-only hosts and tests.</summary>
/// <remarks>
///     Wired via <c>TryAddSingleton</c> by each provider that raises it; the Client host's hub-backed publisher,
///     registered with a plain <c>AddSingleton</c>, wins over it.
/// </remarks>
public sealed class NullRuntimeResidencyChangeNotifier : IRuntimeResidencyChangeNotifier
{
    /// <summary>The shared instance, for a supervisor or gate constructed without a notifier.</summary>
    public static readonly NullRuntimeResidencyChangeNotifier Instance = new();

    /// <inheritdoc />
    public void NotifyChanged()
    {
    }
}
