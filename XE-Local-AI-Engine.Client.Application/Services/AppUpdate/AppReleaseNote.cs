namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>The packaged release notes of one published version, as the last update check read them from the feed.</summary>
/// <remarks>
///     The markdown is untrusted feed text: it is size-capped when read and rendered by the client's sanitizing
///     markdown renderer, never converted to HTML here.
/// </remarks>
public sealed record AppReleaseNote
{
    public required string Version { get; init; }

    public required string NotesMarkdown { get; init; }

    /// <summary>True when the feed's notes were longer than the cap and were cut.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>True when this version is newer than the running one; false for the running version itself.</summary>
    public bool IsNewerThanRunning { get; init; }
}
