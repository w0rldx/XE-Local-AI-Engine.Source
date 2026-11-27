namespace XE_Local_AI_Engine.Tests.ModelLab;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using static ModelLabTestSupport;

/// <summary>The session's hand-overs, observed from inside them: one session over a scripted worker, with no host.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelLabGpuSessionTests
{
    /// <summary>A start that sees the session released must already know the kept receipt, or it admits beside a worker that may hold VRAM.</summary>
    [Test]
    public async Task AnUnseenExitWhileDetachingForTheArms_HandsTheReservationOver_BeforeTheSessionIsReleased()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true,
            ExitsOnKill = false
        };
        var broker = new GpuIdleHolderBroker();
        var gate = Substitute.For<IDisposable>();
        var reservationHandle = Substitute.For<IDisposable>();
        using var reservation = new TrainingCapacityReservation
        {
            Granted = true,
            Reason = null,
            Handle = reservationHandle
        };
        using var workRoot = new TempDirectory("modellab-session");
        var handedOver = new List<string>();
        ModelLabGpuSession? session = null;
        session = new ModelLabGpuSession("key", Guid.NewGuid(), "repo", new ModelLabGpuSessionServices
        {
            Broker = broker,
            Residency = Substitute.For<IRuntimeResidencyChangeNotifier>(),
            IdleClock = TimeProvider.System,
            TimeProvider = TimeProvider.System,
            Logger = NullLogger.Instance,
            LlamaGateHasWaiters = static () => false
        }, (path, _, kept) => handedOver.Add($"{path}:{ReferenceEquals(kept, reservation)}:{session!.State}"));
        session.Register();
        session.Admitted(gate, Substitute.For<ILlamaServerRuntimeMutationLease>(), reservation);
        session.AttachWorker(ModelLabServeWorker.Spawn(process, new ModelLabScriptCommand
        {
            Interpreter = "python",
            ScriptPath = "modellab.py"
        }, workRoot.Path, new ModelLabJobOptions
        {
            EvictGrace = TimeSpan.FromMilliseconds(20),
            ExitVerifyTimeout = TimeSpan.FromMilliseconds(20)
        }, TimeProvider.System), "receipt.json");

        var holds = await session.DetachForArmsAsync().WaitAsync(TestBudgets.Contended);

        AssertEx.Null(holds, "An exit that was not seen passes no holds to the arms.");
        AssertEx.True(process.Killed);
        AssertEx.Equal("receipt.json:True:Evicting", string.Join('|', handedOver), "Handed over once, while the session was still evicting.");
        AssertEx.Equal(ModelLabSessionState.Ended, session.State, "Released only after the hand-over.");
        reservationHandle.DidNotReceive().Dispose();
        gate.Received(1).Dispose();
    }
}
