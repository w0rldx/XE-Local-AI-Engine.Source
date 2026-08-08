namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     A workspace survey refused the directory it was pointed at, rather than surveying something outside the
///     workspace. Distinct from an I/O failure on purpose: an unreadable directory is skipped, whereas this means the
///     path itself was not the one the caller confined — today, a scan root that is a symbolic link.
/// </summary>
public sealed class WorkspaceScanRejectedException : InvalidOperationException
{
    public WorkspaceScanRejectedException(string message) : base(message) { }
}
