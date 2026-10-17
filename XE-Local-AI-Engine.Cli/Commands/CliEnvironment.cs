namespace XE_Local_AI_Engine.Cli.Commands;

using Spectre.Console;
using XE_Local_AI_Engine.Cli.Auth;

/// <summary>What a command reads from and writes to: the process streams in production, captured writers in tests.</summary>
internal sealed class CliEnvironment
{
    /// <summary>Standard output: answers, tables and JSON.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Standard error: diagnostics and notices that must not pollute piped output.</summary>
    public required TextWriter Error { get; init; }

    public required TextReader In { get; init; }

    /// <summary>The console bound to standard output, for tables and the chat session.</summary>
    public required IAnsiConsole Terminal { get; init; }

    /// <summary>The console bound to standard error, for prompts that must not land in piped output (the password).</summary>
    public required IAnsiConsole ErrorTerminal { get; init; }

    /// <summary>Standard input is a terminal a person can answer prompts on.</summary>
    public bool InputIsTerminal { get; init; }

    /// <summary>Standard output is a terminal, so markup and live status lines are wanted.</summary>
    public bool OutputIsTerminal { get; init; }

    public required Func<string, string?> GetVariable { get; init; }

    public required TimeProvider TimeProvider { get; init; }

    public required CliInterrupts Interrupts { get; init; }

    /// <summary>The token file used unless <c>--token-file</c> or <c>--no-token-file</c> says otherwise.</summary>
    public required string DefaultTokenFilePath { get; init; }

    /// <summary>Test seam: a NEW transport handler per call in place of a socket handler.</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; init; }

    public static CliEnvironment FromProcess()
    {
        var interrupts = new CliInterrupts();
        Console.CancelKeyPress += (_, args) =>
        {
            // The command decides what Ctrl-C means (cancel the turn, leave the prompt), so the process stays up.
            args.Cancel = true;
            interrupts.Raise();
        };
        return new CliEnvironment
        {
            Out = Console.Out,
            Error = Console.Error,
            In = Console.In,
            Terminal = AnsiConsole.Console,
            ErrorTerminal = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(Console.Error)
            }),
            InputIsTerminal = !Console.IsInputRedirected,
            OutputIsTerminal = !Console.IsOutputRedirected,
            GetVariable = Environment.GetEnvironmentVariable,
            TimeProvider = TimeProvider.System,
            Interrupts = interrupts,
            DefaultTokenFilePath = TokenFileStore.DefaultPath()
        };
    }
}
