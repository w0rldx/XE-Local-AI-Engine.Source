namespace XE_Local_AI_Engine.Client.Services.Development;

internal interface IDevelopmentWorkspaceTools
{
    IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence { get; }
    DevelopmentCommandProfile Profile { get; }
    Task<string> ListFilesAsync(string? path, CancellationToken cancellationToken = default);
    Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default);
    Task<string> SearchTextAsync(string pattern, string? path, CancellationToken cancellationToken = default);
    Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);
    Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default);
    Task<string> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<string> GetDiffAsync(CancellationToken cancellationToken = default);
    Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default);
}
