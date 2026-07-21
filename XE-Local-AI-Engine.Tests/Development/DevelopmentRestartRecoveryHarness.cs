namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal enum DevelopmentAttemptStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Interrupted,
    Cancelled
}

internal enum DevelopmentArtifactKind
{
    WorkspaceManifest,
    CommandResult,
    ValidationReport,
    ReviewReport
}

public enum DevelopmentInterruptionBoundary
{
    BeforeFirstToken,
    MidStream,
    DuringReadTool,
    AfterWorkspaceWriteBeforeToolResult,
    AfterValidationArtifactBeforeTerminalization
}

internal sealed record DevelopmentProject(Guid Id, string RepositoryIdentity, bool TrustedRepository);

internal sealed class DevelopmentTask
{
    public DevelopmentTask(Guid id, Guid projectId)
    {
        Id = id;
        ProjectId = projectId;
    }

    public Guid Id { get; }

    public Guid ProjectId { get; }

    public bool IsBlocked { get; set; }

    public string? BlockedReason { get; set; }
}

internal sealed class DevelopmentAttempt
{
    public DevelopmentAttempt(Guid id, Guid taskId, DevelopmentAttemptStatus status, Guid? predecessorAttemptId = null)
    {
        Id = id;
        TaskId = taskId;
        Status = status;
        PredecessorAttemptId = predecessorAttemptId;
    }

    public Guid Id { get; }

    public Guid TaskId { get; }

    public Guid? PredecessorAttemptId { get; }

    public DevelopmentAttemptStatus Status { get; set; }
}

internal sealed class DevelopmentArtifact
{
    public DevelopmentArtifact(
        Guid id,
        Guid taskId,
        Guid attemptId,
        DevelopmentArtifactKind kind,
        WorkspaceSnapshot subject,
        string contentHash)
    {
        Id = id;
        TaskId = taskId;
        AttemptId = attemptId;
        Kind = kind;
        BaseCommit = subject.BaseCommit;
        SubjectHash = subject.SubjectHash;
        ManifestHash = subject.ManifestHash;
        ContentHash = contentHash;
    }

    public Guid Id { get; }

    public Guid TaskId { get; }

    public Guid AttemptId { get; }

    public DevelopmentArtifactKind Kind { get; }

    public string BaseCommit { get; }

    public string SubjectHash { get; }

    public string ManifestHash { get; }

    public string ContentHash { get; }

    public bool IsValid { get; set; } = true;
}

internal sealed record DevelopmentEvent(Guid Id, Guid ProjectId, Guid TaskId, Guid? AttemptId, string EventType, long Sequence);

internal sealed record WorkspaceSnapshot(string BaseCommit, string SubjectHash, string ManifestHash, IReadOnlyList<string> ChangedFiles);

internal sealed record DevelopmentRecoveryResult(
    int InterruptedAttempts,
    int InvalidatedArtifacts,
    bool ReplacementAllowed,
    WorkspaceSnapshot CurrentWorkspace);

internal sealed class DevelopmentRestartRecoveryHarness : IAsyncDisposable
{
    private static readonly IReadOnlyList<Type> EntityTypes =
    [
        typeof(DevelopmentProject),
        typeof(DevelopmentTask),
        typeof(DevelopmentAttempt),
        typeof(DevelopmentArtifact),
        typeof(DevelopmentEvent)
    ];

    private readonly string _rootPath;
    private long _nextSequence;

    private DevelopmentRestartRecoveryHarness(string rootPath, string repositoryPath, string worktreePath, string protectedBranchCommit)
    {
        _rootPath = rootPath;
        RepositoryPath = repositoryPath;
        WorktreePath = worktreePath;
        ProtectedBranchCommit = protectedBranchCommit;
        Project = new DevelopmentProject(Guid.NewGuid(), Hash(repositoryPath), TrustedRepository: true);
        Task = new DevelopmentTask(Guid.NewGuid(), Project.Id);
    }

    public string RepositoryPath { get; }

    public string WorktreePath { get; }

    public string ProtectedBranchCommit { get; }

    public DevelopmentProject Project { get; }

    public DevelopmentTask Task { get; }

    public List<DevelopmentAttempt> Attempts { get; } = [];

    public List<DevelopmentArtifact> Artifacts { get; } = [];

    public List<DevelopmentEvent> Events { get; } = [];

    public int WriteCommandExecutions { get; private set; }

    public int ValidationCommandExecutions { get; private set; }

    public int ReadToolExecutions { get; private set; }

    public static IReadOnlyList<Type> PersistentEntityTypes => EntityTypes;

    public static async Task<DevelopmentRestartRecoveryHarness> CreateAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "development-restart-spike-" + Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(rootPath, "repository");
        var worktreePath = Path.Combine(rootPath, "worktree");
        Directory.CreateDirectory(repositoryPath);

        await RunGitAsync(repositoryPath, "init", "--initial-branch=main").ConfigureAwait(false);
        await RunGitAsync(repositoryPath, "config", "user.email", "development-spike@localhost.test").ConfigureAwait(false);
        await RunGitAsync(repositoryPath, "config", "user.name", "Development Restart Spike").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "tracked.txt"), "base\n").ConfigureAwait(false);
        await RunGitAsync(repositoryPath, "add", "tracked.txt").ConfigureAwait(false);
        await RunGitAsync(repositoryPath, "commit", "-m", "base").ConfigureAwait(false);
        var protectedBranchCommit = await RunGitAsync(repositoryPath, "rev-parse", "main").ConfigureAwait(false);
        await RunGitAsync(repositoryPath, "worktree", "add", "-b", "development-spike", worktreePath, "main").ConfigureAwait(false);

        return new DevelopmentRestartRecoveryHarness(rootPath, repositoryPath, worktreePath, protectedBranchCommit);
    }

    public async Task<DevelopmentAttempt> StartAndInterruptAsync(DevelopmentInterruptionBoundary boundary)
    {
        EnsureTrustedRepository();
        var attempt = new DevelopmentAttempt(Guid.NewGuid(), Task.Id, DevelopmentAttemptStatus.Running);
        Attempts.Add(attempt);
        AppendEvent(attempt.Id, "AttemptStarted");
        await AttachWorkspaceManifestAsync(attempt.Id).ConfigureAwait(false);

        switch (boundary)
        {
            case DevelopmentInterruptionBoundary.BeforeFirstToken:
                break;
            case DevelopmentInterruptionBoundary.MidStream:
                AppendEvent(attempt.Id, "SanitizedOutputObserved");
                break;
            case DevelopmentInterruptionBoundary.DuringReadTool:
                ReadToolExecutions++;
                break;
            case DevelopmentInterruptionBoundary.AfterWorkspaceWriteBeforeToolResult:
                await ExecuteFixedWriteCommandAsync().ConfigureAwait(false);
                break;
            case DevelopmentInterruptionBoundary.AfterValidationArtifactBeforeTerminalization:
                await ExecuteFixedValidationCommandAsync(attempt.Id).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundary), boundary, null);
        }

        return attempt;
    }

    public DevelopmentAttempt SeedAttempt(DevelopmentAttemptStatus status)
    {
        var attempt = new DevelopmentAttempt(Guid.NewGuid(), Task.Id, status);
        Attempts.Add(attempt);
        return attempt;
    }

    public async Task<DevelopmentRecoveryResult> RecoverAsync()
    {
        var interruptedAttempts = 0;
        foreach (var attempt in Attempts.Where(candidate => candidate.Status == DevelopmentAttemptStatus.Running))
        {
            attempt.Status = DevelopmentAttemptStatus.Interrupted;
            AppendEvent(attempt.Id, "AttemptInterrupted");
            interruptedAttempts++;
        }

        var currentWorkspace = await CaptureWorkspaceAsync().ConfigureAwait(false);
        var invalidatedArtifacts = 0;
        var evidence = Artifacts.Where(IsGateEvidence).Where(artifact => artifact.IsValid).ToArray();

        if (evidence.Any(artifact => !string.Equals(artifact.BaseCommit, currentWorkspace.BaseCommit, StringComparison.Ordinal)))
        {
            foreach (var artifact in evidence)
            {
                artifact.IsValid = false;
                invalidatedArtifacts++;
            }

            Task.IsBlocked = true;
            Task.BlockedReason = "Workspace base commit cannot be reconciled with persisted evidence.";
            AppendEvent(attemptId: null, "RecoveryBlockedUnreconciledBase");
            return new DevelopmentRecoveryResult(interruptedAttempts, invalidatedArtifacts, ReplacementAllowed: false, currentWorkspace);
        }

        foreach (var artifact in evidence.Where(artifact => !MatchesCurrentSubject(artifact, currentWorkspace)))
        {
            artifact.IsValid = false;
            invalidatedArtifacts++;
            AppendEvent(artifact.AttemptId, "EvidenceInvalidated");
        }

        return new DevelopmentRecoveryResult(interruptedAttempts, invalidatedArtifacts, ReplacementAllowed: !Task.IsBlocked, currentWorkspace);
    }

    public async Task<DevelopmentAttempt> CreateReplacementAttemptAsync(Guid predecessorAttemptId)
    {
        if (Task.IsBlocked)
        {
            throw new InvalidOperationException(Task.BlockedReason);
        }

        var predecessor = Attempts.Single(attempt => attempt.Id == predecessorAttemptId);
        if (predecessor.Status != DevelopmentAttemptStatus.Interrupted)
        {
            throw new InvalidOperationException("Only an interrupted attempt can be replaced.");
        }

        if (Attempts.Any(attempt => attempt.Status == DevelopmentAttemptStatus.Running))
        {
            throw new InvalidOperationException("A replacement cannot start while another attempt is running.");
        }

        var replacement = new DevelopmentAttempt(Guid.NewGuid(), Task.Id, DevelopmentAttemptStatus.Running, predecessorAttemptId);
        Attempts.Add(replacement);
        AppendEvent(replacement.Id, "ReplacementAttemptStarted");
        await AttachWorkspaceManifestAsync(replacement.Id).ConfigureAwait(false);
        return replacement;
    }

    public async Task AttachReviewEvidenceAsync(Guid attemptId)
    {
        var subject = await CaptureWorkspaceAsync().ConfigureAwait(false);
        AttachArtifact(attemptId, DevelopmentArtifactKind.ReviewReport, subject, "approved");
    }

    public async Task MutateWorkspaceOutsideCoordinatorAsync(string content)
    {
        await File.WriteAllTextAsync(Path.Combine(WorktreePath, "tracked.txt"), content).ConfigureAwait(false);
    }

    public async Task CommitWorkspaceMutationOutsideCoordinatorAsync(string content)
    {
        await MutateWorkspaceOutsideCoordinatorAsync(content).ConfigureAwait(false);
        await RunGitAsync(WorktreePath, "add", "tracked.txt").ConfigureAwait(false);
        await RunGitAsync(WorktreePath, "commit", "-m", "unexpected base mutation").ConfigureAwait(false);
    }

    public async Task<WorkspaceSnapshot> CaptureWorkspaceAsync()
    {
        var baseCommit = await RunGitAsync(WorktreePath, "rev-parse", "HEAD").ConfigureAwait(false);
        var patch = await RunGitAsync(WorktreePath, "diff", "--binary", "--full-index", "--no-ext-diff", "HEAD", "--").ConfigureAwait(false);
        var status = await RunGitAsync(WorktreePath, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(false);
        var changedFiles = ParseChangedFiles(status);
        var manifest = await BuildManifestAsync(changedFiles).ConfigureAwait(false);
        var subjectHash = Hash(patch + "\n--manifest--\n" + manifest);
        var manifestHash = Hash(manifest);
        return new WorkspaceSnapshot(baseCommit, subjectHash, manifestHash, changedFiles);
    }

    public async Task<string> ReadProtectedBranchCommitAsync()
    {
        return await RunGitAsync(RepositoryPath, "rev-parse", "main").ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort test cleanup.
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsGateEvidence(DevelopmentArtifact artifact)
    {
        return artifact.Kind is DevelopmentArtifactKind.ValidationReport or DevelopmentArtifactKind.ReviewReport;
    }

    private static bool MatchesCurrentSubject(DevelopmentArtifact artifact, WorkspaceSnapshot subject)
    {
        return string.Equals(artifact.SubjectHash, subject.SubjectHash, StringComparison.Ordinal)
               && string.Equals(artifact.ManifestHash, subject.ManifestHash, StringComparison.Ordinal);
    }

    private async Task ExecuteFixedWriteCommandAsync()
    {
        WriteCommandExecutions++;
        await File.WriteAllTextAsync(Path.Combine(WorktreePath, "tracked.txt"), "base\nchanged by fixed command\n").ConfigureAwait(false);
    }

    private async Task ExecuteFixedValidationCommandAsync(Guid attemptId)
    {
        ValidationCommandExecutions++;
        var subject = await CaptureWorkspaceAsync().ConfigureAwait(false);
        AttachArtifact(attemptId, DevelopmentArtifactKind.ValidationReport, subject, "validation-passed");
    }

    private async Task AttachWorkspaceManifestAsync(Guid attemptId)
    {
        var subject = await CaptureWorkspaceAsync().ConfigureAwait(false);
        AttachArtifact(attemptId, DevelopmentArtifactKind.WorkspaceManifest, subject, "workspace-manifest");
    }

    private void AttachArtifact(Guid attemptId, DevelopmentArtifactKind kind, WorkspaceSnapshot subject, string content)
    {
        Artifacts.Add(new DevelopmentArtifact(Guid.NewGuid(), Task.Id, attemptId, kind, subject, Hash(content)));
    }

    private void AppendEvent(Guid? attemptId, string eventType)
    {
        Events.Add(new DevelopmentEvent(Guid.NewGuid(), Project.Id, Task.Id, attemptId, eventType, ++_nextSequence));
    }

    private void EnsureTrustedRepository()
    {
        if (!Project.TrustedRepository)
        {
            throw new InvalidOperationException("The Process-provider spike requires an explicitly trusted repository.");
        }
    }

    private async Task<string> BuildManifestAsync(IReadOnlyList<string> changedFiles)
    {
        var builder = new StringBuilder();
        foreach (var relativePath in changedFiles.Order(StringComparer.Ordinal))
        {
            var fullPath = Path.Combine(WorktreePath, relativePath);
            var contentHash = File.Exists(fullPath)
                ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false)))
                : "DELETED";
            builder.Append(relativePath).Append('\t').Append(contentHash).Append('\n');
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ParseChangedFiles(string status)
    {
        return status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(line => line.TrimEnd('\r'))
                     .Select(line => line.Length > 3 ? line[3..] : string.Empty)
                     .Select(path => path.Contains(" -> ", StringComparison.Ordinal) ? path[(path.LastIndexOf(" -> ", StringComparison.Ordinal) + 4)..] : path)
                     .Where(path => path.Length > 0)
                     .Order(StringComparer.Ordinal)
                     .ToArray();
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error}");
        }

        return output.TrimEnd('\r', '\n');
    }
}
