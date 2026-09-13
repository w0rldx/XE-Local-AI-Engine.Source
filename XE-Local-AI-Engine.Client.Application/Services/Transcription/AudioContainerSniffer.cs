namespace XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>The audio containers this engine can tell apart from a file's leading bytes.</summary>
public enum AudioContainer
{
    /// <summary>Nothing matched a known signature.</summary>
    Unknown = 0,

    /// <summary>RIFF/WAVE.</summary>
    Wav = 1,

    /// <summary>MPEG audio, either ID3-tagged or a bare frame sync.</summary>
    Mp3 = 2,

    /// <summary>Native FLAC.</summary>
    Flac = 3,

    /// <summary>Ogg (usually Vorbis or Opus).</summary>
    Ogg = 4,

    /// <summary>ISO base media, which is what an <c>.m4a</c> is.</summary>
    Mp4 = 5,

    /// <summary>Matroska, which is what a <c>.webm</c> is.</summary>
    Matroska = 6
}

/// <summary>
///     Decides what an upload actually is from its first bytes. Pure: it performs no I/O and holds no state.
/// </summary>
/// <remarks>
///     <para>
///         The file name extension is never consulted. A client that renames an Ogg file to <c>.wav</c> would
///         otherwise reach the daemon with audio it cannot decode, and the failure would surface as a runtime error
///         rather than as the "this container is not supported" answer the operator can act on.
///     </para>
///     <para>
///         <b>Why MP3 is tested last.</b> Its bare frame sync is eleven bits wide — <c>0xFF</c> followed by the top
///         three bits of the next byte — which a great many byte sequences satisfy by accident. Claiming MP3 only
///         after every structured signature has been ruled out keeps that loose test from shadowing a real container.
///     </para>
/// </remarks>
public static class AudioContainerSniffer
{
    /// <summary>How many leading bytes <see cref="Detect" /> needs at most.</summary>
    public const int HeaderBytes = 16;

    // Ordered exactly as the media types the daemon decodes natively are listed to the operator.
    private static readonly string[] NativeExtensionList = ["wav", "mp3", "flac"];
    private static readonly string[] TranscodeExtensionList = ["ogg", "m4a", "webm"];

    /// <summary>The extensions that reach the runtime untouched.</summary>
    public static IReadOnlyList<string> NativeExtensions => NativeExtensionList;

    /// <summary>The extensions that are accepted only when this engine can transcode them first.</summary>
    public static IReadOnlyList<string> TranscodeExtensions => TranscodeExtensionList;

    /// <summary>Identifies the container from up to <see cref="HeaderBytes" /> leading bytes.</summary>
    public static AudioContainer Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 12 && StartsWith(header, "RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
        {
            return AudioContainer.Wav;
        }

        if (StartsWith(header, "fLaC"u8))
        {
            return AudioContainer.Flac;
        }

        if (StartsWith(header, "OggS"u8))
        {
            return AudioContainer.Ogg;
        }

        if (StartsWith(header, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            return AudioContainer.Matroska;
        }

        if (header.Length >= 8 && header[4..8].SequenceEqual("ftyp"u8))
        {
            return AudioContainer.Mp4;
        }

        // Last, deliberately: see the class remarks.
        if (StartsWith(header, "ID3"u8) || (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0))
        {
            return AudioContainer.Mp3;
        }

        return AudioContainer.Unknown;
    }

    /// <summary>True when the runtime decodes this container itself, with no engine-side conversion.</summary>
    public static bool IsNativelySupported(AudioContainer container) =>
        container is AudioContainer.Wav or AudioContainer.Mp3 or AudioContainer.Flac;

    /// <summary>True when the container reaches the runtime only after this engine transcodes it to WAV.</summary>
    public static bool RequiresFfmpeg(AudioContainer container) =>
        container is AudioContainer.Ogg or AudioContainer.Mp4 or AudioContainer.Matroska;

    /// <summary>The media type to declare for a file of this container.</summary>
    public static string MediaTypeFor(AudioContainer container) => container switch
    {
        AudioContainer.Wav => "audio/wav",
        AudioContainer.Mp3 => "audio/mpeg",
        AudioContainer.Flac => "audio/flac",
        AudioContainer.Ogg => "audio/ogg",
        AudioContainer.Mp4 => "audio/mp4",
        AudioContainer.Matroska => "audio/webm",
        _ => "application/octet-stream"
    };

    private static bool StartsWith(ReadOnlySpan<byte> header, ReadOnlySpan<byte> signature) =>
        header.Length >= signature.Length && header[..signature.Length].SequenceEqual(signature);
}
