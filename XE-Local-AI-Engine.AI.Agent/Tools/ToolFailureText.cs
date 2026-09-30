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

    private const string Prefix = "[tool error: ";

    public static string Format(string code, string message) =>
        $"{Prefix}{code}] {message}";

    /// <summary>The failure code of a result in this shape, or <see langword="null" /> for anything else.</summary>
    public static string? TryGetCode(object? result)
    {
        if (result is not string text || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = text.IndexOf(']', Prefix.Length);
        return end > Prefix.Length ? text[Prefix.Length..end] : null;
    }
}
