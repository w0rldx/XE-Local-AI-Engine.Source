namespace XE_Local_AI_Engine.Tests.Providers.Training;

using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the handshake parse. The probe's stdout is genuinely not clean — importing unsloth prints two banner lines
///     before the JSON is written — so a parser that read the first line, or tried to parse the whole buffer, would
///     fail against the real runtime on this box. Verified live 2026-08-15.
/// </summary>
public sealed class TrainingRuntimeProbeParserTests
{
    [Test]
    public void TryParse_SkipsTheUnslothBannerLinesAndReadsTheHandshake()
    {
        var report = AssertEx.NotNull(TrainingRuntimeProbeParser.TryParse(
            [
                "🦥 Unsloth: Will patch your computer to enable 2x faster free finetuning.",
                "🦥 Unsloth Zoo will now patch everything to make training faster!",
                TrainingRuntimeTestInfrastructure.ValidHandshake
            ]),
            "The handshake must be found after the banner lines.");

        AssertEx.Equal(1, report.ContractVersion);
        AssertEx.True(report.Ready);
        AssertEx.True(report.CudaAvailable);
        AssertEx.Equal("3.13.15", report.PythonVersion);
        AssertEx.Equal("2.11.0+cu128", report.TorchVersion);
        AssertEx.Equal("2026.8.18", report.UnslothVersion);
        AssertEx.Equal("NVIDIA GeForce RTX 5090", report.DeviceName);
        AssertEx.Equal("12.0", report.DeviceCapability);
        AssertEx.Empty(report.Errors);
    }

    [Test]
    public void TryParse_TakesTheLastHandshakeWhenSeveralLinesParse()
    {
        var report = AssertEx.NotNull(TrainingRuntimeProbeParser.TryParse(
            [
                """{"contractVersion":1,"ready":false}""",
                """{"contractVersion":1,"ready":true,"cudaAvailable":true}"""
            ]),
            "A handshake must be parsed.");

        AssertEx.True(report.Ready, "The last handshake line is the probe's actual verdict.");
    }

    [Test]
    public void TryParse_ReadsPerPackageErrorsWhenAnImportFailed()
    {
        var report = AssertEx.NotNull(TrainingRuntimeProbeParser.TryParse(
            [
                """{"contractVersion":1,"ready":false,"cudaAvailable":true,"errors":{"unsloth":"RuntimeError: mismatch","numpy":"ImportError: none"}}"""
            ]),
            "A partial report is still a report.");

        AssertEx.Equal(2, report.Errors.Count);
        AssertEx.Equal("RuntimeError: mismatch", report.Errors["unsloth"]);
    }

    [Test]
    public void TryParse_ReturnsNullWhenNoLineIsAHandshake()
    {
        AssertEx.Null(TrainingRuntimeProbeParser.TryParse(["not json", "{}", """{"ready":true}""", "{ broken"]));
    }

    [Test]
    public void TryParse_ReturnsNullForAnEmptyCapture()
    {
        AssertEx.Null(TrainingRuntimeProbeParser.TryParse([]));
    }
}
