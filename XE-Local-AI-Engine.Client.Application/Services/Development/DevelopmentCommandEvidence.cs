namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.Json.Serialization;

/// <param name="TestOutcome">
///     The structured test result, or null when the command produces none; a code-owned
///     <see cref="IDevelopmentTestResultAdapter" /> reads it from the raw output before truncation
///     (<see cref="Implementation.DevelopmentWorkspaceTools.ExecuteCatalogAsync" />).
/// </param>
internal sealed record DevelopmentCommandEvidence(
    string CommandId,
    int ExitCode,
    bool Completed,
    bool OutputTruncated,
    long DurationMilliseconds,
    string StandardOutput,
    string StandardError,
    DevelopmentTestOutcome? TestOutcome = null)
{
    /// <summary>The standard error of a command the operator stopped, replacing the provider's generic cancel or timeout text.</summary>
    public const string StoppedByOperatorMessage = "Command was stopped by the operator before completion.";

    /// <summary>Whether the operator stopped this command, set from the stop request itself; a command printing the stop text is not a stop.</summary>
    [JsonIgnore]
    public bool StoppedByOperator { get; init; }
}
