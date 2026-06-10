namespace XE_Local_AI_Engine.Installer.Tests.Fakes;

using XE_Local_AI_Engine.Installer.Driver.Windows;

/// <summary>
///     Records every external-process invocation (file name, argument list, stdin) and returns a
///     scripted <see cref="ProcessRunResult" /> chosen by a caller-supplied responder. Lets tests
///     assert the EXACT process contract the driver constructs without launching anything.
/// </summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly Func<ProcessInvocation, ProcessRunResult> _responder;

    public RecordingProcessRunner(Func<ProcessInvocation, ProcessRunResult>? responder = null)
    {
        _responder = responder ?? (_ => Success());
    }

    public List<ProcessInvocation> Invocations { get; } = [];

    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken = default)
    {
        var invocation = new ProcessInvocation(fileName, [.. arguments], standardInput);
        Invocations.Add(invocation);
        return Task.FromResult(_responder(invocation));
    }

    public static ProcessRunResult Success(string stdout = "") =>
        new() { ExitCode = 0, StandardOutput = stdout, StandardError = string.Empty };

    public static ProcessRunResult Failure(int exitCode, string stderr) =>
        new() { ExitCode = exitCode, StandardOutput = string.Empty, StandardError = stderr };
}

/// <summary>One captured process invocation.</summary>
internal sealed record ProcessInvocation(string FileName, IReadOnlyList<string> Arguments, string? StandardInput)
{
    public string ArgumentLine => string.Join(' ', Arguments);

    public bool ArgsContainSequence(params string[] sequence)
    {
        for (var start = 0; start + sequence.Length <= Arguments.Count; start++)
        {
            var match = true;
            for (var offset = 0; offset < sequence.Length; offset++)
            {
                if (!string.Equals(Arguments[start + offset], sequence[offset], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
