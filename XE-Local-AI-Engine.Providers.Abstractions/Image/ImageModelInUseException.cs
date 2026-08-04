namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Thrown when an installed image model cannot be removed because its weight files are still held by something
///     else — in practice the running <c>sd-server</c>, which keeps the model it is serving open.
/// </summary>
/// <remarks>
///     This exists so the failure is a <b>conflict</b> rather than a silent success. The delete path deliberately keeps
///     the registry entry when it cannot remove the weights: dropping it would make the model disappear from the UI
///     while tens of gigabytes stayed on disk, with no remaining way to retry. The operator's fix is to eject the image
///     runtime and delete again, which the message says.
/// </remarks>
public sealed class ImageModelInUseException : Exception
{
    public ImageModelInUseException()
        : this("The model weights are still in use and could not be deleted.")
    {
    }

    public ImageModelInUseException(string message)
        : base(message)
    {
    }

    public ImageModelInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
