namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed record DevelopmentChangedFile(string Path, string ChangeType);

internal sealed record DevelopmentPatchEvidence(
    string BaseCommit,
    string PatchHash,
    string ManifestHash,
    string SubjectHash,
    byte[] PatchBytes,
    byte[] ManifestBytes,
    IReadOnlyList<DevelopmentChangedFile> ChangedFiles);

internal interface IDevelopmentPatchEvidenceService
{
    Task<DevelopmentPatchEvidence> ExportAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentPatchEvidenceService : IDevelopmentPatchEvidenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DevelopmentOptions _options;
    private readonly ISandboxRuntimeProvider _sandbox;

    public DevelopmentPatchEvidenceService(ISandboxRuntimeProvider sandbox, IOptions<DevelopmentOptions> options)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<DevelopmentPatchEvidence> ExportAsync(DevelopmentWorkspaceSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        _ = await RunGitAsync(session, "evidence-stage", ["add", "-A", "--", "."], cancellationToken).ConfigureAwait(false);
        var patch = await RunGitAsync(session,
            "evidence-patch",
            ["diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "HEAD", "--", "."],
            cancellationToken).ConfigureAwait(false);
        var status = await RunGitAsync(session,
            "evidence-status",
            ["diff", "--cached", "--name-status", "-z", "HEAD", "--", "."],
            cancellationToken).ConfigureAwait(false);

        var patchBytes = Encoding.UTF8.GetBytes(patch.StandardOutput);
        if (patchBytes.Length == 0 || patchBytes.Length > _options.MaxPatchBytes)
        {
            throw new InvalidOperationException("The final Development patch is empty or exceeds the configured patch limit.");
        }

        var changedFiles = ParseStatus(status.StandardOutput);
        if (changedFiles.Count == 0 || changedFiles.Count > _options.MaxChangedFiles)
        {
            throw new InvalidOperationException("The final Development changed-file manifest is empty or exceeds the configured file limit.");
        }

        foreach (var item in changedFiles)
        {
            var confined = DevelopmentWorkspaceSecurity.Confine(item.Path, allowRoot: false);
            if (!confined.IsAccepted)
            {
                throw new DevelopmentWorkspaceSecurityException("The final patch contains a protected or escaping path.");
            }
        }

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(changedFiles.OrderBy(static item => item.Path, StringComparer.Ordinal), JsonOptions);
        var patchHash = Hash(patchBytes);
        var manifestHash = Hash(manifestBytes);
        var subjectHash = Hash(Encoding.UTF8.GetBytes(string.Concat(session.BaseCommit, "\n", patchHash, "\n", manifestHash, "\n")));
        return new DevelopmentPatchEvidence(session.BaseCommit,
            patchHash,
            manifestHash,
            subjectHash,
            patchBytes,
            manifestBytes,
            changedFiles);
    }

    private async Task<SandboxCommandResult> RunGitAsync(DevelopmentWorkspaceSession session,
        string executionId,
        IReadOnlyList<string> tail,
        CancellationToken cancellationToken)
    {
        var result = await _sandbox.ExecuteAsync(session.SandboxHandle, new SandboxCommandRequest
        {
            ExecutionId = executionId + "-" + Guid.NewGuid().ToString("N"),
            Executable = AgentHomeGit.Executable,
            Arguments = AgentHomeGit.Arguments([.. tail]),
            WorkingDirectory = "/",
            Timeout = TimeSpan.FromSeconds(_options.MaxAttemptDurationSeconds)
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Completed || result.ExitCode != 0)
        {
            throw new InvalidOperationException("The exact Development patch evidence could not be exported.");
        }

        return result;
    }

    private static IReadOnlyList<DevelopmentChangedFile> ParseStatus(string status)
    {
        var tokens = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<DevelopmentChangedFile>();
        for (var index = 0; index < tokens.Length;)
        {
            var code = tokens[index++];
            if (index >= tokens.Length)
            {
                throw new InvalidDataException("The Git changed-file manifest was truncated.");
            }

            var path = tokens[index++];
            if ((code.StartsWith('R') || code.StartsWith('C')) && index < tokens.Length)
            {
                path = tokens[index++];
            }

            result.Add(new DevelopmentChangedFile(path, ChangeType(code)));
        }

        return result.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static string ChangeType(string status) => status.Length == 0
        ? "unknown"
        : status[0] switch
        {
            'A' => "added",
            'M' => "modified",
            'D' => "deleted",
            'R' => "renamed",
            'C' => "copied",
            'T' => "typechanged",
            _ => "unknown"
        };

    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));
}
