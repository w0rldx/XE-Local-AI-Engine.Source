namespace XE_Local_AI_Engine.Tests.Capacity;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The real GPU work gate and llama supervisor wired to the real broker, with a <see cref="FakeGpuIdleHolder" /> admitted
///     the way the Model Lab session admits: registered first, then the gate, then the lease without demand.
/// </summary>
internal sealed class IdleHolderHarness : IAsyncDisposable
{
    private readonly List<Func<ValueTask>> _giveBack = [];

    public IdleHolderHarness()
    {
        Gate = new GpuWorkGate(Broker);
        Supervisor = SupervisorFactory.Create(Launcher, idlePreemption: Broker);
    }

    public GpuIdleHolderBroker Broker { get; } = new();

    public GpuWorkGate Gate { get; }

    public FakeProcessLauncher Launcher { get; } = new();

    public LlamaServerProcessSupervisor Supervisor { get; }

    /// <summary>A cold admission that ends Active; <paramref name="evictionMayFinish" /> holds the eviction until the test opens it.</summary>
    public async Task<FakeGpuIdleHolder> AdmitAsync(Task? evictionMayFinish = null)
    {
        IDisposable? handle = null;
        ILlamaServerRuntimeMutationLease? lease = null;
        FakeGpuIdleHolder? holder = null;
        holder = new FakeGpuIdleHolder(async () =>
        {
            if (evictionMayFinish is not null)
            {
                await evictionMayFinish;
            }

            await GiveBackAsync(lease, handle);
            Broker.Deregister(holder!);
        });
        _giveBack.Add(() => GiveBackAsync(lease, handle));

        Broker.Register(holder);
#pragma warning disable CA2000 // Given back by the holder's eviction, or by DisposeAsync when a test leaves it held.
        AssertEx.Succeeded(Gate.TryBeginExclusive(GpuWorkKind.ModelLabExperiment, out handle), "The holder's own gate admission is not refused.");
#pragma warning restore CA2000
        lease = AssertEx.NotNull(await Supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None, raisesDemand: false),
            "Nothing is loaded, so the lease is granted.");
        holder.EnterActive();
        return holder;
    }

    /// <summary>A cold admission whose job has ended with no demand, leaving the holder Idle on the gate and the lease.</summary>
    public async Task<FakeGpuIdleHolder> AdmitIdleAsync(Task? evictionMayFinish = null)
    {
        var holder = await AdmitAsync(evictionMayFinish);
        holder.ExitActive();
        AssertEx.Equal(FakeGpuIdleHolder.HolderState.Idle, holder.State, "No demand arrived, so the job ends Idle.");
        return holder;
    }

    public async ValueTask DisposeAsync()
    {
        // The supervisor's disposal waits for a held lease, so anything a test left held is given back first.
        foreach (var giveBack in _giveBack)
        {
            await giveBack();
        }

        await Supervisor.DisposeAsync();
    }

    private static async ValueTask GiveBackAsync(ILlamaServerRuntimeMutationLease? lease, IDisposable? handle)
    {
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }

        handle?.Dispose();
    }
}
