namespace XE_Local_AI_Engine.Client.Services.Images;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Identifies a missing image job or uploaded image at the <see cref="IImageJobCoordinator" /> and
///     <see cref="UploadedImageService" /> boundary.
/// </summary>
/// <remarks>
///     <c>ResourceNotFoundExceptionHandler</c> answers it with the surface's bodyless 404, so the Images endpoints do not
///     branch on a null lookup themselves.
/// </remarks>
public sealed class ImageNotFoundException : ResourceNotFoundException
{
    public ImageNotFoundException(string message) : base(message)
    {
    }
}
