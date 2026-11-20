namespace XE_Local_AI_Engine.Tests.Training.Runs;

using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The trainer's stdout is noisy (unsloth import banners, torch warnings, merged streams); the parser reads
///     protocol out of it and never mistakes noise for protocol: a misread banner would reset a wedged run's watchdog.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TrainingRunStdioParserTests
{
    [Test]
    public void Parse_ReadsEveryProtocolEvent()
    {
        var handshake = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"handshake","contractVersion":1,"torch":"2.11.0"}"""),
            "The handshake must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Handshake, handshake.Kind);
        AssertEx.Equal(expected: 1, handshake.ContractVersion!.Value);

        var phase = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"phase","phase":"training"}"""), "The phase must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Phase, phase.Kind);
        AssertEx.Equal("training", phase.Phase);

        var progress = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"progress","step":7,"totalSteps":40,"epoch":0.5,"loss":1.25,"lr":0.0002,"vramBytes":123456789}"""),
            "The progress line must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Progress, progress.Kind);
        AssertEx.Equal(expected: 7, progress.Step!.Value);
        AssertEx.Equal(expected: 40, progress.TotalSteps!.Value);
        AssertEx.Equal(expected: 1.25, progress.Loss!.Value);
        AssertEx.Equal(expected: 123456789L, progress.VramBytes!.Value);

        var heartbeat = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"heartbeat","phase":"loading"}"""), "The heartbeat must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Heartbeat, heartbeat.Kind);

        var artifact = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"artifact","kind":"HfAdapterDir","path":"/staged"}"""),
            "The artifact must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Artifact, artifact.Kind);
        AssertEx.Equal("/staged", artifact.Path);

        var done = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"done","cancelled":true}"""), "The done line must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Done, done.Kind);
        AssertEx.True(done.Cancelled, "A cooperative stop reports itself as cancelled.");

        var error = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"error","category":"template","message":"no tool branch"}"""),
            "The error must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Error, error.Kind);
        AssertEx.Equal("no tool branch", error.Message);
    }

    [Test]
    public void Parse_ReadsTheServeWorkersReadyFrame()
    {
        var ready = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"ready","token":"t-1","vramBytes":1024,"reservedBytes":4096}"""),
            "The ready frame must parse.");

        AssertEx.Equal(TrainingStdioEventKind.Ready, ready.Kind);
        AssertEx.Equal(expected: 1024L, ready.VramBytes!.Value);
        AssertEx.Equal(expected: 4096L, ready.ReservedBytes!.Value);
        AssertEx.Equal("t-1", ready.Token);
        AssertEx.Null(ready.JobId, "ready is process-scoped and carries no jobId.");
    }

    [Test]
    [Arguments("""{"event":"handshake","contractVersion":6,"token":"t-1","jobId":"j-1"}""")]
    [Arguments("""{"event":"phase","phase":"tracing","token":"t-1","jobId":"j-1"}""")]
    [Arguments("""{"event":"progress","step":1,"token":"t-1","jobId":"j-1"}""")]
    [Arguments("""{"event":"heartbeat","phase":"tracing","token":"t-1","jobId":"j-1"}""")]
    [Arguments("""{"event":"artifact","path":"/x","token":"t-1","jobId":"j-1"}""")]
    [Arguments("""{"event":"done","cancelled":false,"token":"t-1","jobId":"j-1"}""")]
    [Arguments("""{"event":"error","category":"protocol","token":"t-1","jobId":"j-1"}""")]
    [Arguments("""{"event":"ready","vramBytes":0,"token":"t-1","jobId":"j-1"}""")]
    public void Parse_SurfacesTheRawTokenAndJobIdOnEveryKind(string line)
    {
        var parsed = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull(line), "Every protocol kind must parse.");

        AssertEx.Equal("t-1", parsed.Token);
        AssertEx.Equal("j-1", parsed.JobId);
    }

    /// <summary>The handshake's <c>serve</c> is kept as sent: true, false, or null when absent or not a bool, so a check can tell them apart.</summary>
    [Test]
    [Arguments(",\"serve\":true", "True")]
    [Arguments(",\"serve\":false", "False")]
    [Arguments(",\"serve\":\"true\"", "null")]
    [Arguments("", "null")]
    public void Parse_KeepsTheHandshakesServeFieldAsSent(string serveField, string expected)
    {
        var parsed = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("{\"event\":\"handshake\",\"contractVersion\":6" + serveField + "}"));

        AssertEx.Equal(expected, parsed.Serve?.ToString() ?? "null");
    }

    [Test]
    public void Parse_WhenTheTrainerSendsNoTokenOrJobId_LeavesThemNull()
    {
        var done = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"done","cancelled":false}"""), "The done line must parse.");

        AssertEx.Null(done.Token);
        AssertEx.Null(done.JobId);
    }

    [Test]
    public void Parse_IgnoresBannerAndMalformedLines()
    {
        // Every one of these is something this stack really prints, or a line the parser must not accept as protocol.
        string?[] noise =
        [
            null,
            string.Empty,
            "   ",
            "Unsloth: Will patch your computer to enable 2x faster free finetuning.",
            "Unsloth Zoo will now patch everything to make training faster!",
            "==((====))==  Unsloth 2026.8.18: Fast Llama patching.",
            "{not json at all",
            """{"event":}""",
            """[{"event":"done"}]""",
            """{"contractVersion":1}""",
            """{"event":"unknown-kind"}""",
            """{"event":42}""",
            """  {"nested":{"event":"done"}}"""
        ];

        foreach (var line in noise)
        {
            AssertEx.Null(TrainingRunStdioParser.ParseOrNull(line), $"'{line}' must not be read as a protocol event.");
        }
    }

    [Test]
    public void Parse_ToleratesSurroundingWhitespaceAndMissingOptionalFields()
    {
        var progress = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""   {"event":"progress","step":3}   """),
            "A padded line is still protocol.");

        AssertEx.Equal(expected: 3, progress.Step!.Value);
        // Absent is absent, never zero: a zero loss would render as a real measurement in the UI.
        AssertEx.Null(progress.Loss, "An omitted loss must stay absent.");
        AssertEx.Null(progress.TotalSteps, "An omitted total must stay absent.");
    }

    [Test]
    public void Parse_RejectsAWrongTypedField()
    {
        var progress = AssertEx.NotNull(TrainingRunStdioParser.ParseOrNull("""{"event":"progress","step":"seven"}"""),
            "The event itself still parses.");

        // A string where a number belongs is dropped rather than coerced — a coerced 0 would look like real progress.
        AssertEx.Null(progress.Step, "A non-numeric step must not be coerced.");
    }
}
