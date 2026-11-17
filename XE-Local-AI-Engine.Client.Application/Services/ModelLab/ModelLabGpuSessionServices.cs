namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>The node services every <see cref="ModelLabGpuSession" /> shares; one per experiment service.</summary>
internal sealed class ModelLabGpuSessionServices
{
    public required GpuIdleHolderBroker Broker { get; init; }

    public required IRuntimeResidencyChangeNotifier Residency { get; init; }

    /// <summary>Times the idle TTL. The node clock, unless a test drives it.</summary>
    public required TimeProvider IdleClock { get; init; }

    public required TimeProvider TimeProvider { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>True while a caller waits on the llama runtime gate: demand raised before the session registered, which no preemption reached.</summary>
    public required Func<bool> LlamaGateHasWaiters { get; init; }
}
