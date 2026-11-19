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

    /// <summary>A model id with room for a repository path and file name; an assistant message stores it in plaintext on its run envelope, so it is bounded.</summary>
    public const int MaxModelLength = 256;

    public static readonly string TooLargeDetail =
        string.Create(CultureInfo.InvariantCulture, $"The chat export is larger than the {MaxImportBytes / (1024 * 1024)} MiB this node imports at once.");
}
