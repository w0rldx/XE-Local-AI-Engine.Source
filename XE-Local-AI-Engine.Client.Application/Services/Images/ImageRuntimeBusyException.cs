namespace XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Operator-safe conflict raised when a queued image job cannot be admitted because an exclusive image-runtime
///     mutation is active.
/// </summary>
public sealed class ImageRuntimeBusyException : Exception
{
    public ImageRuntimeBusyException(string message) : base(message)
    {
    }
}
