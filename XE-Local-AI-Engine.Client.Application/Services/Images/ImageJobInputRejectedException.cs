namespace XE_Local_AI_Engine.Client.Services.Images;

/// <summary>An image job's edit parameters were refused at enqueue; the message is fixed and display-safe.</summary>
public sealed class ImageJobInputRejectedException : Exception
{
    public ImageJobInputRejectedException(string message) : base(message)
    {
    }
}
