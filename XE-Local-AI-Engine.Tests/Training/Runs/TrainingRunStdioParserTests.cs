namespace XE_Local_AI_Engine.Tests.Training.Runs;

using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The trainer's stdout is not clean: unsloth prints banners at import, torch warns freely, and both streams are
///     merged into one. These pin that the parser reads protocol out of that noise and never mistakes noise for
///     protocol — a banner misread as an event would reset the inactivity watchdog on a wedged run.
/// </summary>
public sealed class TrainingRunStdioParserTests
{
    [Test]
    public void Parse_ReadsEveryProtocolEvent()
    {
        var handshake = AssertEx.NotNull(TrainingRunStdioParser.TryParse("""{"event":"handshake","contractVersion":1,"torch":"2.11.0"}"""),
            "The handshake must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Handshake, handshake.Kind);
        AssertEx.Equal(expected: 1, handshake.ContractVersion!.Value);

        var phase = AssertEx.NotNull(TrainingRunStdioParser.TryParse("""{"event":"phase","phase":"training"}"""), "The phase must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Phase, phase.Kind);
        AssertEx.Equal("training", phase.Phase);

        var progress = AssertEx.NotNull(
            TrainingRunStdioParser.TryParse("""{"event":"progress","step":7,"totalSteps":40,"epoch":0.5,"loss":1.25,"lr":0.0002,"vramBytes":123456789}"""),
            "The progress line must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Progress, progress.Kind);
        AssertEx.Equal(expected: 7, progress.Step!.Value);
        AssertEx.Equal(expected: 40, progress.TotalSteps!.Value);
        AssertEx.Equal(expected: 1.25, progress.Loss!.Value);
        AssertEx.Equal(expected: 123456789L, progress.VramBytes!.Value);

        var heartbeat = AssertEx.NotNull(TrainingRunStdioParser.TryParse("""{"event":"heartbeat","phase":"loading"}"""), "The heartbeat must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Heartbeat, heartbeat.Kind);

        var artifact = AssertEx.NotNull(TrainingRunStdioParser.TryParse("""{"event":"artifact","kind":"HfAdapterDir","path":"/staged"}"""),
            "The artifact must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Artifact, artifact.Kind);
        AssertEx.Equal("/staged", artifact.Path);

        var done = AssertEx.NotNull(TrainingRunStdioParser.TryParse("""{"event":"done","cancelled":true}"""), "The done line must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Done, done.Kind);
        AssertEx.True(done.Cancelled, "A cooperative stop reports itself as cancelled.");

        var error = AssertEx.NotNull(TrainingRunStdioParser.TryParse("""{"event":"error","category":"template","message":"no tool branch"}"""),
            "The error must parse.");
        AssertEx.Equal(TrainingStdioEventKind.Error, error.Kind);
        AssertEx.Equal("no tool branch", error.Message);
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
            AssertEx.Null(TrainingRunStdioParser.TryParse(line), $"'{line}' must not be read as a protocol event.");
        }
    }

    [Test]
    public void Parse_ToleratesSurroundingWhitespaceAndMissingOptionalFields()
    {
        var progress = AssertEx.NotNull(TrainingRunStdioParser.TryParse("""   {"event":"progress","step":3}   """),
            "A padded line is still protocol.");

        AssertEx.Equal(expected: 3, progress.Step!.Value);
        // Absent is absent, never zero: a zero loss would render as a real measurement in the UI.
        AssertEx.Null(progress.Loss, "An omitted loss must stay absent.");
        AssertEx.Null(progress.TotalSteps, "An omitted total must stay absent.");
    }

    [Test]
    public void Parse_RejectsAWrongTypedField()
    {
        var progress = AssertEx.NotNull(TrainingRunStdioParser.TryParse("""{"event":"progress","step":"seven"}"""),
            "The event itself still parses.");

        // A string where a number belongs is dropped rather than coerced — a coerced 0 would look like real progress.
        AssertEx.Null(progress.Step, "A non-numeric step must not be coerced.");
    }
}
