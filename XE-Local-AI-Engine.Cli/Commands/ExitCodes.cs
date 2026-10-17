namespace XE_Local_AI_Engine.Cli.Commands;

/// <summary>The process exit codes of every <c>xe</c> command; <see cref="HelpText" /> is what <c>--help</c> prints.</summary>
internal static class ExitCodes
{
    public const int Success = 0;

    public const int Failure = 1;

    public const int Usage = 2;

    public const int NodeNotRunning = 3;

    public const int AuthOrVault = 4;

    public const int CancelledByUser = 5;

    public const int InteractionRequired = 6;

    /// <summary>128 + SIGINT: Ctrl-C outside a streaming turn.</summary>
    public const int Interrupted = 130;

    internal const string HelpText =
        "Exit codes: 0 ok, 1 failure, 2 usage, 3 node not running or unreachable, 4 sign-in, setup or vault, "
        + "5 turn cancelled by Ctrl-C, 6 interaction required (approval or question without a terminal), "
        + "130 Ctrl-C outside a turn.";
}
