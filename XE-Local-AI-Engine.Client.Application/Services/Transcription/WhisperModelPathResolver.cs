namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     Resolves where this node keeps its Whisper weights. The single place that turns the transcription provider's
///     relative catalogue paths into absolute ones.
/// </summary>
/// <remarks>
///     Rooted at <see cref="INodeDataDirectory" />, never at the application base directory: in a desktop launch the
///     latter is a volatile single-file extraction directory that does not survive a restart. In every other host it
///     resolves to the content root, where the existing runtime-state packaging rules already keep
///     <c>models/**</c> out of every build and publish glob.
/// </remarks>
public sealed class WhisperModelPathResolver
{
    private readonly INodeDataDirectory _dataDirectory;

    public WhisperModelPathResolver(INodeDataDirectory dataDirectory)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    }

    /// <summary>The directory holding every Whisper weight, one subdirectory per catalogue id.</summary>
    public string ModelsDirectory => Path.Combine(_dataDirectory.Root, "models", "whisper");

    /// <summary>The absolute path of the pinned Silero VAD weights.</summary>
    public string VadFilePath => Path.Combine(ModelsDirectory, "vad", WhisperModelCatalog.VadFileName);

    /// <summary>The absolute path of a catalogue entry's weight file.</summary>
    public string FilePathFor(WhisperModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Path.Combine(ModelsDirectory, WhisperModelCatalog.RelativeFilePath(entry));
    }

    /// <summary>Whether a catalogue entry's weight file is present on disk.</summary>
    public bool IsInstalled(WhisperModelEntry entry) => File.Exists(FilePathFor(entry));

    /// <summary>Whether the pinned VAD file is present on disk.</summary>
    public bool IsVadInstalled() => File.Exists(VadFilePath);
}
