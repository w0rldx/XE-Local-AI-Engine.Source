namespace XE_Local_AI_Engine.Cli.Rendering;

using XE_Local_AI_Engine.Cli.Commands;
using XE_Local_AI_Engine.Cli.Streaming;

/// <summary>How a turn ended for the CLI: its terminal event (null when nothing was live) and what the client did.</summary>
internal sealed class TurnOutcome
{
    public CliStreamEvent? Terminal { get; init; }

    /// <summary>Ctrl-C stopped the turn.</summary>
    public bool UserCancelled { get; init; }

    /// <summary>The turn needed a person (an approval or a question) and no terminal could ask one.</summary>
    public bool InteractionRequired { get; init; }

    public int ExitCode =>
        this switch
        {
            { InteractionRequired: true } => ExitCodes.InteractionRequired,
            { UserCancelled: true } => ExitCodes.CancelledByUser,
            { Terminal: null } or { Terminal.Kind: CliStreamEventKind.Completed } => ExitCodes.Success,
            _ => ExitCodes.Failure
        };
}
