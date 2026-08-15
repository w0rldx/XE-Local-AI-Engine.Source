namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the start-outcome → 409 reason/message mapping shared by the generic source-build and CUDA start endpoints.
///     The two endpoints returned byte-for-byte these strings before the duplicated switches were folded together, and
///     the build-kind label is the only wording that may differ between them.
/// </summary>
public sealed class LlamaCppSourceBuildStartEndpointSupportTests
{
    [Test]
    [Arguments(LlamaCppSourceBuildStartOutcome.AlreadyRunning, "already-building", "A CUDA build is already in progress.")]
    [Arguments(LlamaCppSourceBuildStartOutcome.InsufficientDisk, "disk", "There is not enough free disk space to build the CUDA runtime.")]
    [Arguments(LlamaCppSourceBuildStartOutcome.MissingPrerequisites,
        "prerequisites",
        "One or more build prerequisites are missing; resolve the checklist before building.")]
    [Arguments(LlamaCppSourceBuildStartOutcome.ProcessesRunning,
        "processes-running",
        "Stop or eject all running llama.cpp models before building the runtime.")]
    [Arguments(LlamaCppSourceBuildStartOutcome.RuntimeBusy,
        "runtime-busy",
        "Wait for the active llama.cpp source build or runtime change to finish before starting another build.")]
    public void MapBlocked_ForTheCudaBuildKind_KeepsTheCudaWording(LlamaCppSourceBuildStartOutcome outcome,
        string expectedReason,
        string expectedMessage)
    {
        var blocked = LlamaCppSourceBuildStartEndpointSupport.MapBlocked(outcome, LlamaCppSourceBuildStartEndpointSupport.CudaBuildKind);

        AssertEx.True(blocked.HasValue);
        AssertEx.Equal(expectedReason, blocked!.Value.Reason);
        AssertEx.Equal(expectedMessage, blocked.Value.Message);
    }

    [Test]
    [Arguments(LlamaCppSourceBuildStartOutcome.AlreadyRunning, "already-building", "A source build is already in progress.")]
    [Arguments(LlamaCppSourceBuildStartOutcome.InsufficientDisk, "disk", "There is not enough free disk space to build the source runtime.")]
    [Arguments(LlamaCppSourceBuildStartOutcome.MissingPrerequisites,
        "prerequisites",
        "One or more build prerequisites are missing; resolve the checklist before building.")]
    [Arguments(LlamaCppSourceBuildStartOutcome.ProcessesRunning,
        "processes-running",
        "Stop or eject all running llama.cpp models before building the runtime.")]
    [Arguments(LlamaCppSourceBuildStartOutcome.RuntimeBusy,
        "runtime-busy",
        "Wait for the active llama.cpp source build or runtime change to finish before starting another build.")]
    public void MapBlocked_ForTheSourceBuildKind_KeepsTheGenericWording(LlamaCppSourceBuildStartOutcome outcome,
        string expectedReason,
        string expectedMessage)
    {
        var blocked = LlamaCppSourceBuildStartEndpointSupport.MapBlocked(outcome, LlamaCppSourceBuildStartEndpointSupport.SourceBuildKind);

        AssertEx.True(blocked.HasValue);
        AssertEx.Equal(expectedReason, blocked!.Value.Reason);
        AssertEx.Equal(expectedMessage, blocked.Value.Message);
    }

    [Test]
    public void MapBlocked_WhenStarted_ReturnsNull()
    {
        AssertEx.False(LlamaCppSourceBuildStartEndpointSupport
                       .MapBlocked(LlamaCppSourceBuildStartOutcome.Started, LlamaCppSourceBuildStartEndpointSupport.SourceBuildKind)
                       .HasValue);
    }

    [Test]
    public void MapBlocked_WhenOutcomeIsUnknown_Throws()
    {
        // A new outcome added upstream must fail loudly rather than silently answering 200 or a wrong reason code.
        AssertEx.Throws<InvalidOperationException>(() =>
            LlamaCppSourceBuildStartEndpointSupport.MapBlocked((LlamaCppSourceBuildStartOutcome)99,
                LlamaCppSourceBuildStartEndpointSupport.SourceBuildKind));
    }
}
