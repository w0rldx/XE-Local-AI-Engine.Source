namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

/// <summary>
///     Clamps a failure message to the training tables' declared <c>error_message</c>/<c>smoke_reason</c> column
///     length, normalising a blank message to <c>null</c>. Shared by every training store so the cap stays one number.
/// </summary>
internal static class ErrorMessageTruncation
{
    /// <summary>Matches the training tables' <c>error_message</c> and <c>smoke_reason</c> declared max length.</summary>
    public const int MaxLength = 1024;

    public static string? Truncate(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return message.Length > MaxLength ? message[..MaxLength] : message;
    }
}
