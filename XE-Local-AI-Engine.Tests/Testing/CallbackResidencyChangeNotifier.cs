namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>Runs <see cref="OnChange" />, when set, on the thread that ticks residency: a barrier inside a supervisor's detach.</summary>
internal sealed class CallbackResidencyChangeNotifier : IRuntimeResidencyChangeNotifier
{
    public Action? OnChange { get; set; }

    public void NotifyChanged() =>
        OnChange?.Invoke();
}
