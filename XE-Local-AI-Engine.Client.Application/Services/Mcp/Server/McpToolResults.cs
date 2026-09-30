namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

/// <summary>
///     The one failure shape every inbound tool returns: <c>isError: true</c> over a JSON body carrying snake_case
///     <c>failure_code</c> and <c>display_message</c> — the same two fields the structured responses already carry.
/// </summary>
internal static class McpToolResults
{
    public const string FailureCodeProperty = "failure_code";
    public const string DisplayMessageProperty = "display_message";

    /// <summary>
    ///     The <c>_meta</c> key marking a result whose text is free model output, which <see cref="McpToolCallFilter" /> must
    ///     never read as a typed failure. The filter removes it again, so it never reaches the wire.
    /// </summary>
    public const string FreeTextMetaKey = "xe.local/free-text";

    public static CallToolResult Failure(string failureCode, string displayMessage) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = new JsonObject
                    {
                        [FailureCodeProperty] = failureCode,
                        [DisplayMessageProperty] = displayMessage
                    }.ToJsonString()
                }
            ]
        };

    public static CallToolResult Text(string text) =>
        new()
        {
            Content = [new TextContentBlock { Text = text }],
            Meta = new JsonObject { [FreeTextMetaKey] = true }
        };
}
