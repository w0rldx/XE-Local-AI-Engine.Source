namespace XE_Local_AI_Engine.Tests.Testing;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Builds the REAL <see cref="LlamaCppRuntimeOrchestrationService" /> over the provider contracts a test actually
///     drives, substituting the rest.
/// </summary>
/// <remarks>
///     The orchestration service is a verbatim pass-through with no policy of its own, so wrapping a test's own
///     supervisor / binary manager / variant selector in the real type keeps every assertion pointed at the collaborator
///     the test already controls — the mocking order in <c>docs/wiki/17-writing-tests.md</c> (the real thing first)
///     rather than a second fake of the door. One helper because four suites construct it: the proxy forwarder's, both
///     container-bridge suites, and first-run provisioning's.
/// </remarks>
internal static class SubstitutedRuntimeOrchestration
{
    internal static LlamaCppRuntimeOrchestrationService Over(ILlamaServerProcessSupervisor? supervisor = null,
        ILlamaCppBinaryManager? binaryManager = null,
        IGpuVariantSelector? variantSelector = null,
        IRuntimeAcquisitionStatusRegistry? acquisitionStatus = null) =>
        new(Substitute.For<ICudaBuildPrerequisiteProbe>(),
            Substitute.For<ICudaBuildService>(),
            variantSelector ?? Substitute.For<IGpuVariantSelector>(),
            Substitute.For<IInstalledRuntimeStore>(),
            binaryManager ?? Substitute.For<ILlamaCppBinaryManager>(),
            Substitute.For<ILlamaCppSourceBuildActivity>(),
            Substitute.For<ILlamaCppSourceBuildPrerequisiteProbe>(),
            Substitute.For<ILlamaCppSourceBuildService>(),
            Substitute.For<ILlamaCppUpdateState>(),
            supervisor ?? Substitute.For<ILlamaServerProcessSupervisor>(),
            acquisitionStatus ?? Substitute.For<IRuntimeAcquisitionStatusRegistry>());
}
