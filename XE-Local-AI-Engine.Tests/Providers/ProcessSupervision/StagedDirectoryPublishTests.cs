namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The swap-aside publish every managed runtime acquisition (llama.cpp, whisper.cpp, stable-diffusion.cpp) moves its
///     staged tree in with: the old tree is renamed aside and comes back when the move-in fails.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class StagedDirectoryPublishTests
{
    [Test]
    public async Task Publish_OverAnExistingTargetDir_ReplacesItAndLeavesNoSiblingBehind()
    {
        using var cache = new TempDirectory("xe-staged-publish");
        var targetDir = Path.Combine(cache.Path, "runtime", "cuda");
        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "old.dll"), "old");
        var stagingDir = Directory.CreateDirectory($"{targetDir}.staged.tmp").FullName;
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "new.dll"), "new");

        await StagedDirectoryPublish.PublishAsync(stagingDir, targetDir, CancellationToken.None);

        AssertEx.Equal(expected: "new", await File.ReadAllTextAsync(Path.Combine(targetDir, "new.dll")));
        AssertEx.False(File.Exists(Path.Combine(targetDir, "old.dll")), "The old contents must be fully replaced.");
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(targetDir)!, "cuda*").Where(IsTargetSibling));
    }

    [Test]
    public async Task Publish_WhenTheMoveInIsDeniedTwice_RetriesOnScheduleAndPublishesTheStagedTree()
    {
        // The Windows case: an antivirus handle on fresh files denies the rename for a moment, then lets go.
        using var cache = new TempDirectory("xe-staged-publish");
        var targetDir = Path.Combine(cache.Path, "runtime", "cuda");
        var stagingDir = await StageOverExistingTargetAsync(targetDir);
        var deniedMoveIns = 0;
        var delays = new List<TimeSpan>();

        await StagedDirectoryPublish.PublishCoreAsync(stagingDir, targetDir, CancellationToken.None,
            move: (source, destination) =>
            {
                if (source == stagingDir && deniedMoveIns++ < 2)
                {
                    throw new IOException($"Access to the path '{source}' is denied.");
                }

                Directory.Move(source, destination);
            },
            delay: (pause, _) =>
            {
                delays.Add(pause);
                return Task.CompletedTask;
            });

        AssertEx.Equal(expected: "new", await File.ReadAllTextAsync(Path.Combine(targetDir, "new.dll")));
        AssertEx.False(File.Exists(Path.Combine(targetDir, "old.dll")), "The old contents must be fully replaced.");
        AssertEx.Equal(expected: Schedule(StagedDirectoryPublish.RenameRetryDelays.Take(2)), Schedule(delays));
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(targetDir)!, "cuda*").Where(IsTargetSibling));
    }

    [Test]
    public async Task Publish_WhenTheMoveInIsDeniedOnEveryAttempt_RestoresTheExistingTargetDirAndThrows()
    {
        using var cache = new TempDirectory("xe-staged-publish");
        var targetDir = Path.Combine(cache.Path, "runtime", "cuda");
        var stagingDir = await StageOverExistingTargetAsync(targetDir);
        var moveInAttempts = 0;
        var delays = new List<TimeSpan>();

        var thrown = await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => StagedDirectoryPublish.PublishCoreAsync(stagingDir, targetDir,
            CancellationToken.None,
            move: (source, destination) =>
            {
                if (source == stagingDir)
                {
                    moveInAttempts++;
                    throw new UnauthorizedAccessException($"Access to the path '{source}' is denied.");
                }

                Directory.Move(source, destination);
            },
            delay: (pause, _) =>
            {
                delays.Add(pause);
                return Task.CompletedTask;
            }));

        AssertEx.Contains(thrown.Message, "is denied");
        AssertEx.Equal(StagedDirectoryPublish.RenameRetryDelays.Count + 1, moveInAttempts);
        AssertEx.Equal(expected: Schedule(StagedDirectoryPublish.RenameRetryDelays), Schedule(delays));
        AssertEx.Equal(expected: "old", await File.ReadAllTextAsync(Path.Combine(targetDir, "old.dll")));
        AssertEx.Equal(expected: "new", await File.ReadAllTextAsync(Path.Combine(stagingDir, "new.dll")));
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(targetDir)!, "cuda*").Where(d => d.EndsWith(".aside", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Publish_WhenTheAsideRenameIsDeniedOnce_RetriesItAndPublishes()
    {
        using var cache = new TempDirectory("xe-staged-publish");
        var targetDir = Path.Combine(cache.Path, "runtime", "cuda");
        var stagingDir = await StageOverExistingTargetAsync(targetDir);
        var deniedAsides = 0;
        var delays = new List<TimeSpan>();

        await StagedDirectoryPublish.PublishCoreAsync(stagingDir, targetDir, CancellationToken.None,
            move: (source, destination) =>
            {
                if (source == targetDir && deniedAsides++ < 1)
                {
                    throw new IOException($"Access to the path '{source}' is denied.");
                }

                Directory.Move(source, destination);
            },
            delay: (pause, _) =>
            {
                delays.Add(pause);
                return Task.CompletedTask;
            });

        AssertEx.Equal(expected: "new", await File.ReadAllTextAsync(Path.Combine(targetDir, "new.dll")));
        AssertEx.Equal(expected: Schedule(StagedDirectoryPublish.RenameRetryDelays.Take(1)), Schedule(delays));
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(targetDir)!, "cuda*").Where(IsTargetSibling));
    }

    [Test]
    public void RenameRetryDelays_BackOffFrom250MillisecondsToTwoSecondsOverFiveAttempts()
    {
        AssertEx.Equal(expected: "00:00:00.2500000, 00:00:00.5000000, 00:00:01, 00:00:02", Schedule(StagedDirectoryPublish.RenameRetryDelays));
    }

    [Test]
    public async Task Publish_WhenTheMoveIntoPlaceFails_LeavesTheExistingTargetDirIntact()
    {
        // A missing staging dir makes the move into place fail after the old dir was already dealt with: it must come back untouched.
        using var cache = new TempDirectory("xe-staged-publish");
        var targetDir = Path.Combine(cache.Path, "runtime", "cuda");
        Directory.CreateDirectory(Path.Combine(targetDir, "build", "bin"));
        await File.WriteAllTextAsync(Path.Combine(targetDir, "build", "bin", "server.exe"), "old-server");
        await File.WriteAllTextAsync(Path.Combine(targetDir, "build", "bin", "cudart64_12.dll"), "old-cudart");

        // A missing source never appears, so it is not retried: the injected delay throws if a retry is attempted.
        await AssertEx.ThrowsAsync<DirectoryNotFoundException>(() => StagedDirectoryPublish.PublishCoreAsync($"{targetDir}.missing.tmp", targetDir,
            CancellationToken.None,
            delay: (_, _) => throw new InvalidOperationException("A missing staging dir must not be retried.")));

        AssertEx.Equal(expected: "old-server", await File.ReadAllTextAsync(Path.Combine(targetDir, "build", "bin", "server.exe")));
        AssertEx.Equal(expected: "old-cudart", await File.ReadAllTextAsync(Path.Combine(targetDir, "build", "bin", "cudart64_12.dll")));
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(targetDir)!, "cuda*").Where(IsTargetSibling));
    }

    // Fills an existing variant dir with old.dll and returns a staged sibling holding new.dll.
    private static async Task<string> StageOverExistingTargetAsync(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "old.dll"), "old");
        var stagingDir = Directory.CreateDirectory($"{targetDir}.staged.tmp").FullName;
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "new.dll"), "new");
        return stagingDir;
    }

    private static string Schedule(IEnumerable<TimeSpan> delays)
    {
        return string.Join(", ", delays);
    }

    // A staging (.tmp) or publish-aside (.aside) sibling of the target dir, never the target dir itself.
    private static bool IsTargetSibling(string directory) =>
        Path.GetFileName(directory).StartsWith("cuda.", StringComparison.Ordinal);
}
