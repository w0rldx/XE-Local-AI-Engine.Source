namespace XE_Local_AI_Engine.Installer.Cli;

/// <summary>
///     Console seam for output and the interactive typed confirmation (plan §7.4 / D6). Abstracted so
///     unit tests can capture written lines and script the operator's confirmation answer without a
///     real terminal.
/// </summary>
public interface IInstallerConsole
{
    void WriteLine(string message);

    void WriteError(string message);

    /// <summary>
    ///     Prompt for the irreversible-action typed confirmation. Returns true only when the operator
    ///     types the exact expected token (e.g. <c>yes</c>).
    /// </summary>
    bool ConfirmDestructiveAction(string prompt, string expectedToken);
}
