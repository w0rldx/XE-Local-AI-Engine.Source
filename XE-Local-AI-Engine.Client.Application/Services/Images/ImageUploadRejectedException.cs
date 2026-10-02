namespace XE_Local_AI_Engine.Client.Services.Images;

/// <summary>An uploaded file was refused; the message is fixed, display-safe and names the rule that was broken.</summary>
public sealed class ImageUploadRejectedException : Exception
{
    public ImageUploadRejectedException(string message) : base(message)
    {
    }
}
