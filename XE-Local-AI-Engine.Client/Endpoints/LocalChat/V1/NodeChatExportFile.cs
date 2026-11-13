namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using System.Globalization;

/// <summary>The chat export file's identity and the caps an import of it is held to.</summary>
internal static class NodeChatExportFile
{
    public const string Format = "xe-chat-export";

    public const int FormatVersion = 1;

    /// <summary>64 MiB: years of text-only chats, and still a body the node parses in memory without strain.</summary>
    public const long MaxImportBytes = 64L * 1024 * 1024;

    public const int MaxConversations = 5_000;

    public const int MaxMessages = 100_000;

    public const int MaxTitleLength = 200;

    public static readonly string TooLargeDetail =
        string.Create(CultureInfo.InvariantCulture, $"The chat export is larger than the {MaxImportBytes / (1024 * 1024)} MB this node imports at once.");
}
