namespace XE_Local_AI_Engine.Cli.Tests.Commands;

using System.Text.Json;
using Spectre.Console;
using XE_Local_AI_Engine.Cli.Commands;

/// <summary>A captured <see cref="CliEnvironment" />: writers instead of the process streams, no terminal, scripted variables.</summary>
internal sealed class TestCli : IDisposable
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public TestCli(Func<HttpMessageHandler>? handlerFactory = null,
        IReadOnlyDictionary<string, string>? variables = null,
        string standardInput = "",
        TimeProvider? timeProvider = null)
    {
        Interrupts = new CliInterrupts();
        Environment = new CliEnvironment
        {
            Out = _out,
            Error = _error,
            In = new StringReader(standardInput),
            Terminal = Capture(_out),
            ErrorTerminal = Capture(_error),
            GetVariable = name => variables is not null && variables.TryGetValue(name, out var value) ? value : null,
            TimeProvider = timeProvider ?? TimeProvider.System,
            Interrupts = Interrupts,
            DefaultTokenFilePath = Path.Combine(Path.GetTempPath(), $"xe-cli-tokens-{Guid.NewGuid():N}", "tokens.json"),
            HandlerFactory = handlerFactory
        };
    }

    public CliEnvironment Environment { get; }

    public CliInterrupts Interrupts { get; }

    public string Out => _out.ToString();

    public string Error => _error.ToString();

    /// <summary>Standard output as JSON lines.</summary>
    public IReadOnlyList<JsonElement> JsonLines => [.. Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => JsonDocument.Parse(line).RootElement.Clone())];

    public static IAnsiConsole Capture(TextWriter writer) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            // Spectre's built-in CI enrichers (GITHUB_ACTIONS and friends) override these settings, e.g. Ansi=true on a runner.
            Enrichment = new ProfileEnrichment
            {
                UseDefaultEnrichers = false
            }
        });

    public Task<int> RunAsync(params string[] args) =>
        CliRoot.RunAsync(args, Environment, CancellationToken.None);

    public void Dispose()
    {
        _out.Dispose();
        _error.Dispose();
        Interrupts.Dispose();
    }
}
