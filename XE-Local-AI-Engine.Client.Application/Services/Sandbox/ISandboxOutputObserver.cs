namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>Receives each output line of a running sandbox command as the provider's pump reads it, for a live view only.</summary>
/// <remarks>
///     Observe-only: the captured result, its truncation and its timing are the same with or without an observer. Calls
///     arrive on the pump threads (stdout and stderr concurrently), so an implementation is thread-safe, enqueues and
///     returns. A provider catches an observer's exception, logs it once and stops forwarding to that observer.
/// </remarks>
public interface ISandboxOutputObserver
{
    /// <summary>One line without its terminator (<c>\n</c>, <c>\r\n</c> or a lone <c>\r</c>), raw and unredacted.</summary>
    void OnLine(SandboxOutputSource source, string line);
}

/// <summary>Which child stream a line came from.</summary>
public enum SandboxOutputSource
{
    /// <summary>The child's standard output.</summary>
    StandardOutput,

    /// <summary>The child's standard error.</summary>
    StandardError
}
