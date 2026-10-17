namespace XE_Local_AI_Engine.Cli.Commands;

/// <summary>A command stops with <see cref="ExitCode" />; the message is fit to print as is.</summary>
public sealed class CliCommandException : Exception
{
    public CliCommandException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
