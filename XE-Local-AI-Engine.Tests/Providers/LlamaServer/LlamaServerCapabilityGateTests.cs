namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The capability gate's treatment of <c>--cpu-moe</c>. The flag is not an optimization the gate may drop: the
///     admission ledger booked this process's footprint on the premise that the whole expert share sits in system RAM,
///     so a runtime that cannot take the flag cannot honour that booking and the launch is REFUSED — never degraded,
///     and never with a safe fallback, which would drop the flag and launch the very over-subscription being prevented.
/// </summary>
public sealed class LlamaServerCapabilityGateTests
{
    private const string HelpWithCpuMoe = """
                                          -m, --model FNAME
                                          --host HOST
                                          --port PORT
                                          -c, --ctx-size N
                                          --parallel N
                                          --no-warmup
                                          --fit [on|off]
                                          --metrics
                                          --jinja
                                          --cache-ram N
                                          -cmoe, --cpu-moe
                                          """;

    private const string HelpWithoutCpuMoe = """
                                             -m, --model FNAME
                                             --host HOST
                                             --port PORT
                                             -c, --ctx-size N
                                             --parallel N
                                             --no-warmup
                                             --fit [on|off]
                                             --metrics
                                             --jinja
                                             --cache-ram N
                                             """;

    [Test]
    public void Primary_NeverLaunchesInExpertOffloadWithoutTheFlag()
    {
        // On a runtime that DOES advertise the flag the launch proceeds and the flag survives the gate untouched:
        // --cpu-moe is deliberately absent from OptionalOptions, so no code path can quietly strip it while the
        // process still launches. An unsupported optional flag beside it is still dropped, proving the gate ran.
        var decision = LlamaServerCapabilityGate.Apply(Spec(["--cpu-moe", "--cache-reuse", "256", "--fit", "on"]),
            Manifest(HelpWithCpuMoe),
            requireMetrics: false);

        AssertEx.True(decision.IsCompatible, "a runtime advertising --cpu-moe must accept an expert-offload launch.");
        AssertEx.Contains(decision.Spec.Arguments, "--cpu-moe", "the gate must never remove the flag that makes the placement true.");
        AssertEx.Contains(decision.OmittedOptions, "--cache-reuse", "an unsupported OPTIONAL flag is still dropped, so the gate really ran.");
    }

    [Test]
    public void CpuMoe_OnARuntimeThatDoesNotAdvertiseIt_IsRefusedWithoutSafeFallback()
    {
        var decision = LlamaServerCapabilityGate.Apply(Spec(["--cpu-moe", "--fit", "on"]),
            Manifest(HelpWithoutCpuMoe),
            requireMetrics: false);

        AssertEx.False(decision.IsCompatible, "a runtime without --cpu-moe cannot honour the offload footprint that was admitted.");
        AssertEx.False(decision.CanTrySafeFallback,
            "a safe fallback would drop the flag and launch outside the reserved VRAM — the refusal must be final.");
        AssertEx.Contains(AssertEx.NotNull(decision.SanitizedError), LlamaServerCapabilityGate.ExpertOffloadRequiresCpuMoe);
        AssertEx.Empty(decision.OmittedOptions, "a refusal must not claim it omitted anything.");
    }

    [Test]
    public void WithoutTheFlag_ARuntimeThatDoesNotAdvertiseItIsUnaffected()
    {
        // Byte-identical default: the new branch is invisible to every launch that does not ask for the flag.
        var decision = LlamaServerCapabilityGate.Apply(Spec(["--fit", "on", "--jinja"]),
            Manifest(HelpWithoutCpuMoe),
            requireMetrics: false);

        AssertEx.True(decision.IsCompatible, "a launch that never asks for --cpu-moe must be unaffected by the new branch.");
    }

    private static LlamaServerCapabilityManifest Manifest(string help) =>
        LlamaServerCapabilityManifest.FromSuccessfulProbe(new LlamaBinary("/opt/llama/llama-server", "b10201", GpuVariant.Cuda, IsPinnedFallback: false),
            executableLengthBytes: 1024,
            DateTimeOffset.UnixEpoch,
            new string('a', 64),
            "version: 10201 (b10201)",
            help);

    private static LlamaServerLaunchSpec Spec(string[] arguments) =>
        new("llama3",
            ModelRole.Chat,
            "/opt/llama/llama-server",
            ["-m", "/models/llama3.gguf", "--host", "127.0.0.1", "--port", "8080", .. arguments],
            8080,
            "/opt/llama");
}
