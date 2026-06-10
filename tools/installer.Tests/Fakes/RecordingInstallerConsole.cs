namespace XE_Local_AI_Engine.Installer.Tests.Fakes;

using XE_Local_AI_Engine.Installer.Cli;

/// <summary>
///     Capturing <see cref="IInstallerConsole" />. Records output lines and returns a scripted answer
///     for the typed confirmation so destructive-gate behavior is testable without a terminal.
/// </summary>
internal sealed class RecordingInstallerConsole : IInstallerConsole
{
    private readonly bool _confirmationAnswer;

    public RecordingInstallerConsole(bool confirmationAnswer = true)
    {
        _confirmationAnswer = confirmationAnswer;
    }

    public List<string> Lines { get; } = [];

    public List<string> Errors { get; } = [];

    public int ConfirmCallCount { get; private set; }

    public void WriteLine(string message) => Lines.Add(message);

    public void WriteError(string message) => Errors.Add(message);

    public bool ConfirmDestructiveAction(string prompt, string expectedToken)
    {
        ConfirmCallCount++;
        return _confirmationAnswer;
    }

    public bool ContainsLine(string fragment) =>
        Lines.Concat(Errors).Any(line => line.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
