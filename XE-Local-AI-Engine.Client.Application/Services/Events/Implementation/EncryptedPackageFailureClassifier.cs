namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

/// <summary>
///     Classifies decryption <see cref="InvalidOperationException" /> reasons raised while assembling an encrypted
///     runtime package envelope (AAD / config-hash / history-hash mismatch).
/// </summary>
internal static class EncryptedPackageFailureClassifier
{
    internal static bool IsAadMismatch(InvalidOperationException exception)
    {
        return exception.Message.Contains("AAD", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsConfigHashMismatch(InvalidOperationException exception)
    {
        return exception.Message.Contains("runtime-package-config-hash-mismatch", StringComparison.Ordinal);
    }

    internal static bool IsHistoryHashMismatch(InvalidOperationException exception)
    {
        return exception.Message.Contains("runtime-package-history-hash-mismatch", StringComparison.Ordinal);
    }
}
