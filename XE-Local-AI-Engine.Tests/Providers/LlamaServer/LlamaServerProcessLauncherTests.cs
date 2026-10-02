namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The forwarded-line level policy: marker lines stay at Information, the rest goes to Debug, and one slot line per
///     interval per process is promoted so a long turn's progress stays visible.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LlamaServerProcessLauncherTests
{
    private const string SlotLine = "slot update_slots: id  0 | task 7 | prompt processing progress, n_past = 2048, n_tokens = 2048, progress = 0.5";

    [Test]
    public void TryPromoteServingLine_PromotesOneSlotLinePerInterval()
    {
        var lastPromoted = -LlamaServerProcessLauncher.PromotedLineIntervalMilliseconds;

        AssertEx.True(LlamaServerProcessLauncher.TryPromoteServingLine(SlotLine, nowMilliseconds: 0, ref lastPromoted));
        AssertEx.False(LlamaServerProcessLauncher.TryPromoteServingLine(SlotLine, nowMilliseconds: 4999, ref lastPromoted),
            "a second slot line inside the interval must stay at Debug");
        AssertEx.True(LlamaServerProcessLauncher.TryPromoteServingLine(SlotLine, nowMilliseconds: 5000, ref lastPromoted),
            "the next slot line once the interval has passed must be promoted again");
    }

    [Test]
    [Arguments("0.06.876.442 I slot   load_model: id  0 | task -1 | new slot, n_ctx = 65536")]
    [Arguments("12.31.004.118 I slot update_slots: id  0 | task 7 | prompt processing progress, n_past = 2048, progress = 0.5")]
    [Arguments("slot print_timing: id  0 | task 7 | prompt eval time = 812.44 ms")]
    public void TryPromoteServingLine_MatchesSlotLinesWithAndWithoutTheVerbosityPrefix(string line)
    {
        // Under -lv llama.cpp prefixes every line with "<elapsed> <level>"; the shipped tester log carries that shape,
        // and a bare-prefix match alone never fired on it.
        var lastPromoted = -LlamaServerProcessLauncher.PromotedLineIntervalMilliseconds;

        AssertEx.True(LlamaServerProcessLauncher.TryPromoteServingLine(line, nowMilliseconds: 0, ref lastPromoted));
    }

    [Test]
    [Arguments("0.00.162.385 I srv          init: running without SSL")]
    [Arguments("0.06.919.504 I srv  update_slots: all slots are idle")]
    [Arguments("12.31.004.120 D que          post: new task, id = 9, front = 0")]
    [Arguments("12.31.004.121 D res          send: sending result for task id = 9")]
    [Arguments("0.00.200.081 I srv    load_model: loading model 'C:\\models\\unsloth\\slot-test.gguf'")]
    public void TryPromoteServingLine_NeverPromotesPrefixedNonSlotLines(string line)
    {
        var lastPromoted = -LlamaServerProcessLauncher.PromotedLineIntervalMilliseconds;

        AssertEx.False(LlamaServerProcessLauncher.TryPromoteServingLine(line, nowMilliseconds: 0, ref lastPromoted));
    }

    [Test]
    public void TryPromoteServingLine_NeverPromotesNonSlotChatter()
    {
        var lastPromoted = -LlamaServerProcessLauncher.PromotedLineIntervalMilliseconds;

        AssertEx.False(LlamaServerProcessLauncher.TryPromoteServingLine("llama_model_loader: - kv  12: general.name str = Qwen", nowMilliseconds: 0, ref lastPromoted));
        AssertEx.False(LlamaServerProcessLauncher.TryPromoteServingLine("que    post: new task, id = 9", nowMilliseconds: 0, ref lastPromoted));
        AssertEx.True(LlamaServerProcessLauncher.TryPromoteServingLine(SlotLine, nowMilliseconds: 0, ref lastPromoted),
            "dropped chatter must not consume the slot line's budget");
    }

    // The recorded start-up transcript holds exactly this many marker lines; a regex drift changes the count.
    private const int FixtureMarkerLines = 22;

    [Test]
    public async Task IsMarkerLine_OnARecordedLoadLog_KeepsExactlyTheMarkerLines()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "llama-server-load.log"));

        AssertEx.True(lines.Length > 60, "the fixture must be copied to the output directory");
        AssertEx.Equal(FixtureMarkerLines, lines.Count(LlamaServerProcessLauncher.IsMarkerLine));
    }

    [Test]
    [Arguments("load_tensors: offloaded 25/25 layers to GPU")]
    [Arguments("print_info: model type       = 1B")]
    [Arguments("print_info: file size   = 373.71 MiB (6.35 BPW)")]
    [Arguments("ggml_cuda_init: found 1 CUDA devices:")]
    [Arguments("main: server is listening on http://127.0.0.1:51234 - starting the main loop")]
    [Arguments("0.06.919.504 I srv  update_slots: all slots are idle")]
    [Arguments("W warning: something odd")]
    [Arguments("cudaMalloc failed: Out Of Memory")]
    public void IsMarkerLine_MatchesMarkers(string line)
    {
        AssertEx.True(LlamaServerProcessLauncher.IsMarkerLine(line));
    }

    [Test]
    [Arguments("llama_model_loader: - kv  12:                          general.file_type u32              = 15")]
    [Arguments("print_info: n_layer          = 24")]
    [Arguments("12.31.004.120 D que          post: new task, id = 9, front = 0")]
    public void IsMarkerLine_LeavesChatterAtDebug(string line)
    {
        AssertEx.False(LlamaServerProcessLauncher.IsMarkerLine(line));
    }
}
