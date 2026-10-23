namespace XE_Local_AI_Engine.Desktop;

/// <summary>Picks the notice for an engine that exited with a failure, from its exit code and its stderr tail.</summary>
internal static class EngineExitNotice
{
    /// <summary>The engine's one-line stderr report when an application control policy blocked one of its files.</summary>
    internal const string BlockedPrefix = "Application control blocked: ";

    // Where the engine's report ends the file and starts its explanation.
    private const string BlockedSuffix = ". Windows Smart App Control";

    internal static bool IsApplicationControlBlock(int? exitCode, string? errorTail) =>
        exitCode == DesktopApplication.ApplicationControlBlockedExitCode || BlockedLine(errorTail) is not null;

    /// <summary>The blocked file the engine named, or null when its report is missing or names none.</summary>
    internal static string? BlockedFile(string? errorTail)
    {
        if (BlockedLine(errorTail) is not { } line) { return null; }

        // The engine writes "<file>. Windows Smart App Control ..."; the file can hold dots, spaces and commas (an assembly
        // display name), so only that fixed suffix ends it; without the suffix the whole remainder is the file.
        var end = line.IndexOf(BlockedSuffix, StringComparison.Ordinal);
        var file = (end < 0 ? line : line[..end]).Trim();
        return file.Length == 0 ? null : file;
    }

    /// <summary>The application control notice for a blocked file, else the generic one.</summary>
    internal static string For(int? exitCode, string? errorTail, string installFolder) =>
        IsApplicationControlBlock(exitCode, errorTail)
            ? DesktopText.ApplicationControlBlocked(BlockedFile(errorTail), installFolder)
            : DesktopText.EngineStopped;

    // The pre-ready failure quotes the tail after its own sentence, so the report need not start a line there.
    private static string? BlockedLine(string? errorTail)
    {
        var start = errorTail?.IndexOf(BlockedPrefix, StringComparison.Ordinal) ?? -1;
        if (start < 0) { return null; }

        var rest = errorTail![(start + BlockedPrefix.Length)..];
        var end = rest.IndexOfAny(['\r', '\n']);
        return end < 0 ? rest : rest[..end];
    }
}
