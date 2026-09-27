namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Where the pipeline's intermediate and final files live inside the run's staged directory.</summary>
internal static class TrainingExportPaths
{
    /// <summary>
    ///     Staged file names carry the canonical quant token because that is what the GGUF inspector reads a
    ///     quantization off when the header does not declare one — an adapter's header never does.
    /// </summary>
    public static string MergedGgufName(string quantization) =>
        $"merged-{quantization}.gguf";

    public static string AdapterGgufName() =>
        $"adapter-{TrainingExportQuantizations.Float16}.gguf";

    /// <summary>Reads back the quantization the export named a staged file with.</summary>
    public static string? QuantizationOf(string stagedPath) =>
        GgufQuantParser.TryParse(Path.GetFileName(stagedPath));
}
