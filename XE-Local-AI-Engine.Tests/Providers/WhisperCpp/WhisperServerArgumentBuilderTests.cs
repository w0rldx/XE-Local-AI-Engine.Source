namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The argument builder is the single enforcement point for the rule that no whisper-server flag escapes this
///     project, so these cases are the guard for it. The most load-bearing one is
///     <see cref="Build_NeverEmitsConvertOrTmpDir" />: the convert flag would make the daemon write EVERY request's
///     audio to disk, including live PCM, which is the contract this whole feature is built around.
/// </summary>
public sealed class WhisperServerArgumentBuilderTests
{
    private const string ModelPath = "/models/whisper/base/ggml-base.bin";
    private const string ExecutablePath = "/opt/whisper/bin/whisper-server";

    [Test]
    public void Build_CpuBackend_EmitsNoGpuAndThreadCount()
    {
        var spec = Build(WhisperBackend.Cpu, Options());

        AssertEx.Contains(spec.Arguments, "-ng", "A CPU backend must tell the server not to offload.");
        var threadIndex = spec.Arguments.ToList().IndexOf("-t");
        AssertEx.True(threadIndex >= 0, "A CPU backend must pin a thread count.");
        AssertEx.Equal("8", spec.Arguments[threadIndex + 1]);
    }

    [Test]
    public void Build_CudaBackend_OmitsNoGpuAndThreads()
    {
        var spec = Build(WhisperBackend.Cuda, Options());

        AssertEx.False(spec.Arguments.Contains("-ng"), "A GPU backend must not be told to skip the GPU.");
        AssertEx.False(spec.Arguments.Contains("-t"), "A thread count is only worth pinning when the CPU does the work.");
    }

    [Test]
    public void Build_VadModelConfigured_EmitsVadAndVadModelTogether()
    {
        var spec = Build(WhisperBackend.Cpu, Options(vadModelPath: "/models/whisper/vad/ggml-silero-v6.2.0.bin"));

        var vadIndex = spec.Arguments.ToList().IndexOf("--vad");
        var modelIndex = spec.Arguments.ToList().IndexOf("-vm");
        AssertEx.True(vadIndex >= 0, "VAD must be switched on when a VAD model is configured.");
        AssertEx.True(modelIndex >= 0, "The VAD model path must be passed when VAD is switched on.");
        AssertEx.Equal("/models/whisper/vad/ggml-silero-v6.2.0.bin", spec.Arguments[modelIndex + 1]);
    }

    [Test]
    public void Build_VadModelMissing_EmitsNeitherFlag()
    {
        // Half a VAD configuration is worse than none: -vm alone configures a model nothing consults, and --vad alone
        // leaves the server hunting for a file nobody named.
        var spec = Build(WhisperBackend.Cpu, Options(vadModelPath: null));

        AssertEx.False(spec.Arguments.Contains("--vad"));
        AssertEx.False(spec.Arguments.Contains("-vm"));
    }

    [Test]
    [Arguments(WhisperBackend.Cpu)]
    [Arguments(WhisperBackend.Cuda)]
    public void Build_NeverEmitsConvertOrTmpDir(WhisperBackend backend)
    {
        // Asserted over the FULL argument vector for EVERY backend. With convert on, the daemon writes every request's
        // audio to a temp file and shells out to ffmpeg — including for a native 16 kHz mono WAV, and including every
        // live tick. There is no option that can turn this on, and this is the test that keeps it that way.
        var spec = Build(backend, Options(vadModelPath: "/models/whisper/vad/ggml-silero-v6.2.0.bin"));

        AssertEx.False(spec.Arguments.Contains("--convert"), "The daemon must never be launched with audio conversion on.");
        AssertEx.False(spec.Arguments.Contains("--tmp-dir"), "A temp directory only relocates a file that is never written.");
    }

    [Test]
    [Arguments(WhisperBackend.Cpu)]
    [Arguments(WhisperBackend.Cuda)]
    public void Build_NeverEmitsDiarizeOrPrintRealtime(WhisperBackend backend)
    {
        // -di splits a stereo WAV by channel and is not speaker clustering; per-channel transcription is used instead.
        // -pr/-pp print the transcript to a stream this project drains straight into the app log.
        var spec = Build(backend, Options());

        AssertEx.False(spec.Arguments.Contains("-di"));
        AssertEx.False(spec.Arguments.Contains("--diarize"));
        AssertEx.False(spec.Arguments.Contains("-pr"));
        AssertEx.False(spec.Arguments.Contains("-pp"));
    }

    [Test]
    public void Build_BindsLoopbackHostAndAllocatedPort()
    {
        var spec = Build(WhisperBackend.Cpu, Options());
        var args = spec.Arguments.ToList();

        AssertEx.Equal("127.0.0.1", args[args.IndexOf("--host") + 1]);
        AssertEx.Equal("18311", args[args.IndexOf("--port") + 1]);
        AssertEx.Equal(expected: 18311, spec.Port);
        AssertEx.Equal("http://127.0.0.1:18311/", spec.BaseAddress.AbsoluteUri);
    }

    [Test]
    public void Build_PassesTheModelPathAndRunsFromTheBinaryDirectory()
    {
        var spec = Build(WhisperBackend.Cpu, Options());
        var args = spec.Arguments.ToList();

        AssertEx.Equal(ModelPath, args[args.IndexOf("-m") + 1]);
        // The working directory is the binary's own, so co-located runtime libraries resolve through $ORIGIN.
        AssertEx.Equal(AssertEx.NotNull(Path.GetDirectoryName(Path.GetFullPath(ExecutablePath))), spec.WorkingDirectory);
    }

    [Test]
    public void Build_AlwaysSuppressesLanguageProbabilities()
    {
        // Computing them is documented as expensive and the server does it per request; the per-request field
        // re-enables them for the one call that needs a detected language.
        AssertEx.Contains(Build(WhisperBackend.Cpu, Options()).Arguments, "-nlp");
    }

    [Test]
    [Arguments(1, 4)]
    [Arguments(8, 4)]
    [Arguments(12, 6)]
    [Arguments(16, 8)]
    [Arguments(128, 8)]
    public void ResolveThreadCount_IsHalfTheCoresBoundedToFourThroughEight(int processorCount, int expected)
    {
        AssertEx.Equal(expected, WhisperServerArgumentBuilder.ResolveThreadCount(processorCount));
    }

    private static WhisperServerLaunchSpec Build(WhisperBackend backend, WhisperRuntimeOptions options) =>
        WhisperServerArgumentBuilder.Build("base", ModelPath, ExecutablePath, backend, port: 18311, options, processorCount: 16);

    private static WhisperRuntimeOptions Options(string? vadModelPath = null) =>
        new()
        {
            VadModelPath = vadModelPath
        };
}
