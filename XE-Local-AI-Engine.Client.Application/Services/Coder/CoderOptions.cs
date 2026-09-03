namespace XE_Local_AI_Engine.Client.Services.Coder;

/// <summary>
///     Worker-side caps for the read-only coder tools (section <c>Coder</c>). These bound every coder read so a single
///     tool call can never exhaust memory, hang, or flood the model context: the per-command timeout and output caps
///     mirror the AgentHome command posture, and the byte/line caps enforce the read-safety controls. The
///     coder tools are themselves gated by <c>AgentHome:Enabled</c> (they share the AgentHome sandbox), so this section
///     carries no enable flag of its own.
/// </summary>
public sealed class CoderOptions
{
    public const string SectionName = "Coder";

    /// <summary>Maximum number of entries a single <c>list_files</c> call returns. Defaults to 500.</summary>
    public int MaxListResults { get; set; } = 500;

    /// <summary>Maximum number of matches a single <c>search_text</c> call returns. Defaults to 200.</summary>
    public int MaxSearchMatches { get; set; } = 200;

    /// <summary>
    ///     Hard per-read byte cap for <c>read_file</c>. A file larger than this is read up to the cap and a truncation
    ///     marker is appended. Defaults to 262144 (256 KiB).
    /// </summary>
    public int MaxReadBytes { get; set; } = 256 * 1024;

    /// <summary>
    ///     Default line cap applied to <c>read_file</c> when the caller supplies no explicit line range, so
    ///     an unbounded read of a large file does not flood the model context. Defaults to 2000.
    /// </summary>
    public int DefaultReadLineCap { get; set; } = 2000;

    /// <summary>
    ///     Hard byte cap on what a single <c>search_text</c> call returns, on top of
    ///     <see cref="MaxSearchMatches" />. Both are needed: a match count alone does not bound the output of a file
    ///     whose lines are enormous, and a generated or minified file routinely is. Defaults to 262144 (256 KiB).
    /// </summary>
    public int MaxSearchOutputBytes { get; set; } = 256 * 1024;

    /// <summary>Per-call timeout for the list/search workspace surveys. Defaults to 30 seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
}
