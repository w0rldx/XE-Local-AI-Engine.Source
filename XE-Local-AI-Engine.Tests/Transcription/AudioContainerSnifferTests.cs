namespace XE_Local_AI_Engine.Tests.Transcription;

using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins what the engine decides an upload is from its leading bytes. The extension is deliberately not an input to
///     anything here: a client that renames an Ogg file to <c>.wav</c> must still be told it sent Ogg.
/// </summary>
public sealed class AudioContainerSnifferTests
{
    // One header per container, then the three ways nothing should match, then the case that pins the ordering.
    [Test]
    [Arguments("524946462400000057415645666d7420", AudioContainer.Wav, "RIFF with WAVE at offset eight")]
    [Arguments("664c614300000022100010000000", AudioContainer.Flac, "fLaC stream marker")]
    [Arguments("4f67675300020000000000000000", AudioContainer.Ogg, "OggS page header")]
    [Arguments("1a45dfa3010000000000002342", AudioContainer.Matroska, "EBML header, which is what a webm is")]
    [Arguments("00000020667479704d34412000000000", AudioContainer.Mp4, "ftyp at offset four, which is what an m4a is")]
    [Arguments("494433040000000000000000fffb9000", AudioContainer.Mp3, "ID3-tagged MP3")]
    [Arguments("fffb9000", AudioContainer.Mp3, "bare MPEG frame sync")]
    [Arguments("", AudioContainer.Unknown, "an empty file")]
    [Arguments("524946", AudioContainer.Unknown, "three bytes, too few for any signature")]
    [Arguments("524946462400000041564920666d7420", AudioContainer.Unknown, "RIFF without WAVE — a RIFF AVI is not audio this node takes")]
    public void Detect_IdentifiesTheContainerFromItsSignature(string headerHex, AudioContainer expected, string because)
    {
        var detected = AudioContainerSniffer.Detect(Convert.FromHexString(headerHex));

        AssertEx.Equal(expected, detected, $"Expected {expected} for {because}.");
    }

    [Test]
    public void Detect_WhenAFrameSyncPrecedesARealSignature_PrefersTheRealSignature()
    {
        // The MPEG frame sync is eleven bits wide, so plenty of bytes satisfy it by accident. This header would be
        // claimed as MP3 by a sniffer that tested it first; it is an ISO base-media file.
        var header = Convert.FromHexString("ffffffff66747970" + "4d34412000000000");

        AssertEx.Equal(AudioContainer.Mp4,
            AudioContainerSniffer.Detect(header),
            "MP3's loose frame sync must never shadow a structured container signature.");
    }

    [Test]
    public void NativeAndTranscodeSets_PartitionTheKnownContainers()
    {
        AssertEx.True(AudioContainerSniffer.IsNativelySupported(AudioContainer.Wav)
                      && AudioContainerSniffer.IsNativelySupported(AudioContainer.Mp3)
                      && AudioContainerSniffer.IsNativelySupported(AudioContainer.Flac),
            "WAV, MP3 and FLAC reach the runtime untouched.");
        AssertEx.True(AudioContainerSniffer.RequiresFfmpeg(AudioContainer.Ogg)
                      && AudioContainerSniffer.RequiresFfmpeg(AudioContainer.Mp4)
                      && AudioContainerSniffer.RequiresFfmpeg(AudioContainer.Matroska),
            "Ogg, MP4 and Matroska are accepted only when this engine can convert them first.");

        foreach (var container in Enum.GetValues<AudioContainer>())
        {
            AssertEx.False(AudioContainerSniffer.IsNativelySupported(container) && AudioContainerSniffer.RequiresFfmpeg(container),
                $"{container} cannot be both native and conversion-only.");
        }

        AssertEx.False(AudioContainerSniffer.IsNativelySupported(AudioContainer.Unknown)
                       || AudioContainerSniffer.RequiresFfmpeg(AudioContainer.Unknown),
            "An unidentified container belongs to neither set, so it is refused.");
    }

    [Test]
    public void SupportedExtensionLists_NameTheContainersTheSnifferAccepts()
    {
        AssertEx.Equal("wav,mp3,flac", string.Join(',', AudioContainerSniffer.NativeExtensions));
        AssertEx.Equal("ogg,m4a,webm", string.Join(',', AudioContainerSniffer.TranscodeExtensions));
    }
}
