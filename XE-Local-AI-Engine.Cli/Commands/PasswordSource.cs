namespace XE_Local_AI_Engine.Cli.Commands;

using Spectre.Console;

/// <summary>
///     The admin password, read at most once per run: <c>XE_ADMIN_PASSWORD</c>, then one standard-input line with
///     <c>--password-stdin</c>, then a hidden prompt on standard error when standard input is a terminal. Never argv.
/// </summary>
internal sealed class PasswordSource
{
    internal const string EnvironmentVariable = "XE_ADMIN_PASSWORD";

    private readonly bool _fromStandardInput;
    private readonly CliEnvironment _environment;
    private Task<string?>? _read;

    public PasswordSource(bool fromStandardInput, CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _fromStandardInput = fromStandardInput;
        _environment = environment;
    }

    /// <summary>The password, or null when none of the sources can supply one.</summary>
    public Task<string?> GetAsync(CancellationToken cancellationToken) =>
        _read ??= ReadAsync(cancellationToken);

    private async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (_environment.GetVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        if (_fromStandardInput)
        {
            return await _environment.In.ReadLineAsync(cancellationToken) is { Length: > 0 } line ? line : null;
        }

        if (!_environment.InputIsTerminal)
        {
            return null;
        }

        var prompt = new TextPrompt<string>("Admin password:").Secret();
        return await _environment.ErrorTerminal.PromptAsync(prompt, cancellationToken);
    }
}
