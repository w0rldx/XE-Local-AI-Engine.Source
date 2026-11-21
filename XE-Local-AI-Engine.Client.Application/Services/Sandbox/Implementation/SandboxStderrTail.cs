namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>
///     The last few lines a long-lived sandboxed command wrote to <c>stderr</c>, bounded by line count and by characters,
///     with every configured environment value redacted as it arrives.
/// </summary>
/// <remarks>
///     Bounded twice because a wedged server can write without limit. The redaction is by VALUE: a server that echoes
///     its own token in a startup error must not carry that token into an exception, a log line or the UI.
/// </remarks>
internal sealed class SandboxStderrTail
{
    internal const int MaxLines = 20;
    internal const int MaxCharacters = 2048;

    /// <summary>What an incomplete line is kept as: a value cut inside it no longer matches its redaction, so none of it is shown.</summary>
    internal const string WithheldLine = "[incomplete line withheld]";

    // Values shorter than this are not redacted, so a debug flag of one digit does not turn every such digit in
    // the tail into a placeholder; a real credential is longer. Lower it only together with a smarter matcher.
    private const int MinSecretLength = 8;

    private readonly Lock _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly SecretValueRedactor _redactor;
    private int _characters;

    public SandboxStderrTail(IEnumerable<string>? secretValues)
        : this(new SecretValueRedactor(SecretValueRedactor.WithParts(secretValues ?? [], MinSecretLength)))
    {
    }

    private SandboxStderrTail(SecretValueRedactor redactor)
    {
        _redactor = redactor;
    }

    /// <summary>
    ///     A tail redacting what <paramref name="request" /> itself carries: every environment value but the search path, and
    ///     every argument but a fully qualified path, since a server's token is as often a command-line flag as a variable.
    /// </summary>
    public static SandboxStderrTail For(SandboxCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environment = (request.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal)).Where(static pair => !SecretValueRedactor.IsSearchPathKey(pair.Key))
                                                                                                         .Select(static pair => pair.Value);
        return new SandboxStderrTail(new SecretValueRedactor(SecretValueRedactor.ForCommand(environment, request.Arguments, MinSecretLength)));
    }

    /// <summary>Appends one redacted line, or <see cref="WithheldLine" /> for an incomplete one, evicting the oldest until both bounds hold again.</summary>
    public void Append(string? line, bool complete)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var redacted = complete ? _redactor.Redact(line.TrimEnd()) : WithheldLine;
        if (redacted.Length > MaxCharacters)
        {
            redacted = redacted[^MaxCharacters..];
        }

        lock (_gate)
        {
            _lines.Enqueue(redacted);
            _characters += redacted.Length;
            while (_lines.Count > MaxLines || _characters > MaxCharacters)
            {
                _characters -= _lines.Dequeue().Length;
            }
        }
    }

    /// <summary>The retained lines, oldest first, or <see langword="null" /> when the command wrote nothing.</summary>
    public string? Snapshot()
    {
        lock (_gate)
        {
            return _lines.Count == 0 ? null : string.Join('\n', _lines);
        }
    }
}
