namespace XE_Local_AI_Engine.Installer.Cli;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     Production <see cref="IInstallerConsole" /> over <see cref="Console" />. Errors go to stderr;
///     the typed confirmation reads a line from stdin and compares it ordinally (case-insensitively)
///     to the expected token. Console I/O in a CLI is intentionally synchronous (the contract is sync
///     and the confirmation blocks on stdin), so the async-write analyzer rules are suppressed here.
/// </summary>
[SuppressMessage("Reliability", "CA1849:Call async methods when in an async method", Justification = "Synchronous CLI console I/O; the IInstallerConsole contract is synchronous.")]
public sealed class SystemInstallerConsole : IInstallerConsole
{
    public void WriteLine(string message) => Console.Out.WriteLine(message);

    public void WriteError(string message) => Console.Error.WriteLine(message);

    public bool ConfirmDestructiveAction(string prompt, string expectedToken)
    {
        Console.Out.Write(prompt);
        var answer = Console.In.ReadLine();
        return string.Equals(answer?.Trim(), expectedToken, StringComparison.OrdinalIgnoreCase);
    }
}
