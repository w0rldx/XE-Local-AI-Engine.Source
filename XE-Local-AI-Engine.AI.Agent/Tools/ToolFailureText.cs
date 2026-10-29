namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     The one text shape of a tool failure that is RETURNED as a result instead of thrown: a
///     <c>[tool error: code]</c> prefix the model can read, followed by the explanation.
/// </summary>
/// <remarks>
///     A thrown tool exception reaches the model as MEAI's fixed "Error: Function failed." under
///     <c>IncludeDetailedErrors=false</c>, so a wrapper that wants the model to see WHY returns this text instead. The
///     prefix is also how the observability hop and the chat stream tell such a result from a success, since the
///     function-invocation loop records no exception for it.
/// </remarks>
internal static class ToolFailureText
{
    /// <summary>The per-call deadline fired before the tool answered.</summary>
    public const string TimeoutCode = "timeout";

    /// <summary>The tool ran and reported its own failure (an MCP <c>isError</c> result).</summary>
    public const string ToolReportedCode = "tool_reported";

    /// <summary>The sandbox stopped the tool for exceeding its memory or disk limit.</summary>
    public const string ResourceLimitCode = "resource_limit";

    /// <summary>
    ///     Appended to a refusal that stopped the call before the tool started, so a small model neither retries nor
    ///     invents a result. Never on a failure after the tool started: that one may have done something.
    /// </summary>
    public const string NotRunNotice =
        "Nothing ran and there is no result. Do not repeat this call in this turn; answer without it and say that the tool was not run.";

    private const string Prefix = "[tool error: ";

    public static string Format(string code, string message) =>
        $"{Prefix}{code}] {message}";

    /// <summary><see cref="Format" /> for a refusal before the tool started, ending in <see cref="NotRunNotice" />.</summary>
    public static string FormatNotRun(string code, string message) =>
        Format(code, $"{message} {NotRunNotice}");

    /// <summary>The failure code of a result in this shape, or <see langword="null" /> for anything else.</summary>
    public static string? GetCodeOrNull(object? result)
    {
        if (result is not string text || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = text.IndexOf(']', Prefix.Length);
        return end > Prefix.Length ? text[Prefix.Length..end] : null;
    }
}
