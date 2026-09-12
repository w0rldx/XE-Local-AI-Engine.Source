namespace XE_Local_AI_Engine.Client.Services.Transcription;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for local audio transcription.
///     <para>
///         <see cref="Enabled" /> gates <em>behaviour</em>, never registration — the same posture work sessions and
///         graph workflows hold. The endpoints stay discovered when the feature is off, so the OpenAPI document, and
///         therefore the generated client, is identical on every node; a request-path middleware answers 404 instead.
///     </para>
/// </summary>
public sealed class TranscriptionOptions
{
    public const string Section = "Transcription";

    /// <summary>Whether the transcription surface answers at all. On by default.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     The appsettings SEED for the idle time-to-live of the whisper daemon, in minutes. The stored node setting
    ///     wins over this; it exists so a first run has a value before anything has been saved.
    /// </summary>
    [Range(1, 240)]
    public int IdleTimeoutMinutes { get; init; } = 15;
}
