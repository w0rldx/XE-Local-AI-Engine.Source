namespace XE_Local_AI_Engine.Client.Services.Transcription;

using System.Globalization;

/// <summary>Raised when an upload exceeds the node's configured maximum file size.</summary>
/// <remarks>
///     The cap is enforced while the body is being read, not from a declared length: a streamed upload has no length
///     to inspect beforehand, and a cap checked afterwards has already let every byte reach the disk.
/// </remarks>
public sealed class TranscriptionUploadTooLargeException : Exception
{
    public TranscriptionUploadTooLargeException(string message) : base(message)
    {
    }

    /// <summary>The one refusal text for an upload past <paramref name="maxBytes" />, whichever layer refused it.</summary>
    public static string MessageFor(long maxBytes) =>
        string.Create(CultureInfo.InvariantCulture, $"The uploaded audio is larger than the {maxBytes / (1024 * 1024)} MB this node accepts.");
}
