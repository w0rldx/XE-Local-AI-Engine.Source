namespace XE_Local_AI_Engine.Installer.Cli;

/// <summary>
///     Outcome of parsing the process arguments. Exactly one of <see cref="Arguments" /> (on success)
///     or <see cref="ErrorMessage" /> (on failure) is populated. A failed parse always carries a
///     non-empty message and the usage text so <c>Program</c> can print and exit non-zero.
/// </summary>
public sealed record ArgumentParseResult
{
    private ArgumentParseResult(bool isSuccess, InstallerArguments? arguments, string? errorMessage, string usage)
    {
        IsSuccess = isSuccess;
        Arguments = arguments;
        ErrorMessage = errorMessage;
        Usage = usage;
    }

    public bool IsSuccess { get; }

    public InstallerArguments? Arguments { get; }

    public string? ErrorMessage { get; }

    public string Usage { get; }

    public static ArgumentParseResult Success(InstallerArguments arguments, string usage) =>
        new(isSuccess: true, arguments, errorMessage: null, usage);

    public static ArgumentParseResult Failure(string errorMessage, string usage) =>
        new(isSuccess: false, arguments: null, errorMessage, usage);
}
