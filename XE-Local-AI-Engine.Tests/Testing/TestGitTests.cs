namespace XE_Local_AI_Engine.Tests.Testing;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Testing.Git;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     <see cref="TestGit" /> against real git: output past the pipe buffer on either stream (which hangs a runner that
///     drains them one after the other), an exported repository selector or config override, and cancellation.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TestGitTests
{
    private const int PastThePipeBuffer = 256 * 1024;

    [Test]
    public async Task RunAsync_ReturnsAStandardOutputLargerThanThePipeBuffer()
    {
        using var directory = new TempDirectory("test-git");
        await TestGit.RunAsync(directory.Path, "init", "--quiet", ".");
        var payload = new string('x', PastThePipeBuffer);
        await File.WriteAllTextAsync(directory.FilePath("payload.txt"), payload);
        var blob = (await TestGit.RunAsync(directory.Path, "hash-object", "-w", "payload.txt")).Trim();

        var output = await TestGit.RunAsync(directory.Path, "cat-file", "blob", blob).WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(payload, output);
    }

    [Test]
    public async Task RunAsync_OnANonZeroExitWithAStandardErrorLargerThanThePipeBuffer_FailsWithThatErrorInTheMessage()
    {
        using var directory = new TempDirectory("test-git");
        await TestGit.RunAsync(directory.Path, "init", "--quiet", ".");
        // Each unmatched pathspec prints its own error line: 2000 of them pass the pipe buffer on stderr alone.
        var missing = Enumerable.Range(0, 2000).Select(static index => $"missing-{index:D4}").ToArray();

        var failure = await AssertEx.ThrowsAsync<AssertionException>(() =>
            TestGit.RunAsync(directory.Path, ["ls-files", "--error-unmatch", .. missing]).WaitAsync(TestBudgets.Contended));

        // git's own wording follows the host locale; the last pathspec after the marker can only come from stderr.
        var stderr = failure.Message[failure.Message.IndexOf("failed:", StringComparison.Ordinal)..];
        AssertEx.Contains(stderr, "missing-1999");
    }

    [Test]
    public async Task RunAsync_WhenTheTokenCancelsAGitWaitingOnInput_ThrowsOperationCanceled()
    {
        using var directory = new TempDirectory("test-git");
        using var cancellation = new CancellationTokenSource();

        // hash-object --stdin blocks until stdin closes, which a run never does on its own.
        var run = TestGit.RunAsync(directory.Path, cancellation.Token, "hash-object", "--stdin");
        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TestBudgets.Contended));
    }

    /// <summary>
    ///     The alias's shell backgrounds a helper that inherits both pipes, waits until git has exited and been reaped,
    ///     writes its pid, then holds the pipes far past the run's budget, as a hook's background job can.
    /// </summary>
    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task RunAsync_WhenTheTokenCancelsAfterGitExitedWhileAHelperHoldsItsPipes_ThrowsOperationCanceled()
    {
        using var directory = new TempDirectory("test-git");
        await TestGit.RunAsync(directory.Path, "init", "--quiet", ".");
        var helperPid = directory.FilePath("helper.pid");
        const string Linger =
            "alias.linger=!sh -c 'while kill -0 \"$1\" 2>/dev/null; do sleep 0.05; done; echo $$ > helper.pid.tmp; "
            + "mv helper.pid.tmp helper.pid; exec sleep 600' helper $PPID &";
        using var cancellation = new CancellationTokenSource();

        var run = TestGit.RunAsync(directory.Path, cancellation.Token, "-c", Linger, "linger");
        try
        {
            await AssertEx.EventuallyAsync(() => File.Exists(helperPid), TestBudgets.Contended, "the helper never saw git exit");
            await cancellation.CancelAsync();

            await AssertEx.CompletesAsync(
                AssertEx.ThrowsAsync<OperationCanceledException>(() => run),
                TestBudgets.Contended,
                "the run kept waiting on pipes a helper holds after git exited, past its caller's cancellation");
        }
        finally
        {
            if (File.Exists(helperPid))
            {
                using var helper = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(helperPid)).Trim(), CultureInfo.InvariantCulture));
                helper.Kill();
            }
        }
    }

    [Test]
    public async Task RepositorySelectors_AreTheListTheScriptsDrop()
    {
        const string Opening = "XE_GIT_REPO_ENV_VARS=(";
        var script = await File.ReadAllTextAsync(RepositoryPaths.Combine("scripts", "lib", "git-env.sh"));
        var start = script.IndexOf(Opening, StringComparison.Ordinal);
        AssertEx.True(start >= 0, $"scripts/lib/git-env.sh no longer declares {Opening}");
        start += Opening.Length;
        var listed = script[start..script.IndexOf(')', start)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        AssertEx.Equal(
            string.Join(' ', listed.Order(StringComparer.Ordinal)),
            string.Join(' ', TestGitEnvironment.RepositorySelectors.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     A focused <c>dotnet test</c> run inherits whatever its shell exported; fixture setup must still only touch
    ///     the fixture. Run alone: every git this process starts sees the variable while it is set.
    /// </summary>
    [Test]
    [NotInParallel]
    [Arguments("GIT_INDEX_FILE")]
    [Arguments("GIT_DIR")]
    [Arguments("GIT_CONFIG")]
    public async Task RunAsync_WithAnExportedRepositorySelector_LeavesTheSelectedRepositoryByteForByteUnchanged(string selector)
    {
        using var external = new TempDirectory("test-git-external");
        await TestGit.RunAsync(external.Path, "init", "--quiet", ".");
        await File.WriteAllTextAsync(external.FilePath("staged.txt"), "staged");
        await TestGit.RunAsync(external.Path, "add", "staged.txt");
        await File.WriteAllTextAsync(external.FilePath("sentinel.gitconfig"), "[sentinel]\n\tkept = true\n");
        var before = Snapshot(external.Path);
        using var fixture = new TempDirectory("test-git");
        await File.WriteAllTextAsync(fixture.FilePath("fixture.txt"), "fixture");
        var selected = selector switch
        {
            "GIT_DIR" => external.FilePath(".git"),
            "GIT_CONFIG" => external.FilePath("sentinel.gitconfig"),
            _ => external.FilePath(Path.Combine(".git", "index"))
        };

        var previous = Environment.GetEnvironmentVariable(selector);
        Environment.SetEnvironmentVariable(selector, selected);
        try
        {
            await TestGit.RunAsync(fixture.Path, "init", "--quiet", "--initial-branch=main", ".");
            await TestGit.RunAsync(fixture.Path, "config", "user.email", "test-git@example.invalid");
            await TestGit.RunAsync(fixture.Path, "config", "user.name", "Test Git");
            await TestGit.RunAsync(fixture.Path, "add", "-A", "--", ".");
            await TestGit.RunAsync(fixture.Path, "commit", "--quiet", "-m", "fixture");
        }
        finally
        {
            Environment.SetEnvironmentVariable(selector, previous);
        }

        AssertEx.Equal(before, Snapshot(external.Path), $"fixture git followed the exported {selector} into another repository");
        AssertEx.Equal("fixture.txt\n", await TestGit.RunAsync(fixture.Path, "ls-tree", "--name-only", "HEAD"));
        AssertEx.Equal("test-git@example.invalid\n", await TestGit.RunAsync(fixture.Path, "config", "--local", "user.email"));
    }

    /// <summary>
    ///     Config a shell or a parent git exported through the environment outranks the fixture's own repository
    ///     config. Run alone, as the variables reach every git this process starts while they are set.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task RunAsync_WithExportedConfigInjection_CommitsWithTheFixtureIdentity()
    {
        using var fixture = new TempDirectory("test-git");
        await File.WriteAllTextAsync(fixture.FilePath("fixture.txt"), "fixture");
        const string Injected = "injected@example.invalid";
        string[] variables = ["GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"];
        var previous = variables.ToDictionary(static name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", $"'user.email'='{Injected}'");
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "user.email");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", Injected);
        try
        {
            await TestGit.RunAsync(fixture.Path, "init", "--quiet", "--initial-branch=main", ".");
            await TestGit.RunAsync(fixture.Path, "config", "user.email", "test-git@example.invalid");
            await TestGit.RunAsync(fixture.Path, "config", "user.name", "Test Git");
            await TestGit.RunAsync(fixture.Path, "add", "-A", "--", ".");
            await TestGit.RunAsync(fixture.Path, "commit", "--quiet", "-m", "fixture");
        }
        finally
        {
            foreach (var entry in previous)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        AssertEx.Equal("test-git@example.invalid\n", await TestGit.RunAsync(fixture.Path, "log", "-1", "--format=%ae"));
    }

    /// <summary>
    ///     An operator's global or system config with a rejecting commit hook and commit and tag signing must not reach a
    ///     fixture commit or tag; the system file's other settings still apply. Run alone: the variable reaches every git.
    /// </summary>
    [Test]
    [NotInParallel]
    [Arguments("GIT_CONFIG_GLOBAL")]
    [Arguments("GIT_CONFIG_SYSTEM")]
    public async Task RunAsync_WithAnInheritedConfigThatHooksAndSigns_CommitsAndTagsWithoutRunningEither(string variable)
    {
        using var operatorConfig = new TempDirectory("test-git-operator");
        var hooks = Directory.CreateDirectory(operatorConfig.FilePath("hooks")).FullName;
        var hookRan = operatorConfig.FilePath("hook-ran");
        var hook = Path.Combine(hooks, "pre-commit");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\ntouch '{hookRan.Replace('\\', '/')}'\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var configFile = operatorConfig.FilePath("operator.gitconfig");
        var missingGpg = operatorConfig.FilePath("missing-gpg").Replace('\\', '/');
        await File.WriteAllTextAsync(configFile,
            $"[core]\n\thooksPath = {hooks.Replace('\\', '/')}\n[commit]\n\tgpgSign = true\n[tag]\n\tgpgSign = true\n"
            + $"[gpg]\n\tprogram = {missingGpg}\n[sentinel]\n\tkept = true\n");
        // The other scope holds a conflicting operator setting, which an unscoped probe would read instead.
        var conflicting = variable == "GIT_CONFIG_GLOBAL" ? "GIT_CONFIG_SYSTEM" : "GIT_CONFIG_GLOBAL";
        var conflictingFile = operatorConfig.FilePath("conflicting.gitconfig");
        await File.WriteAllTextAsync(conflictingFile, "[commit]\n\tgpgSign = false\n");
        using var fixture = new TempDirectory("test-git");
        await File.WriteAllTextAsync(fixture.FilePath("fixture.txt"), "fixture");

        var previous = Environment.GetEnvironmentVariable(variable);
        var previousConflicting = Environment.GetEnvironmentVariable(conflicting);
        Environment.SetEnvironmentVariable(variable, configFile);
        Environment.SetEnvironmentVariable(conflicting, conflictingFile);
        try
        {
            // Non-vacuity: a git outside the fixture runner reads the planted file through the inherited variable.
            AssertEx.Equal("true\n", await RawGitConfigAsync(fixture.Path, variable, conflicting, "commit.gpgsign"));

            await TestGit.RunAsync(fixture.Path, "init", "--quiet", "--initial-branch=main", ".");
            await TestGit.RunAsync(fixture.Path, "config", "user.email", "test-git@example.invalid");
            await TestGit.RunAsync(fixture.Path, "config", "user.name", "Test Git");
            await TestGit.RunAsync(fixture.Path, "add", "-A", "--", ".");
            await TestGit.RunAsync(fixture.Path, "commit", "--quiet", "-m", "fixture");
            await TestGit.RunAsync(fixture.Path, "tag", "-a", "-m", "fixture", "v1");
            if (variable == "GIT_CONFIG_SYSTEM")
            {
                // The system file stays readable: Git for Windows keeps its platform defaults there.
                AssertEx.Equal("true\n", await TestGit.RunAsync(fixture.Path, "config", "--get", "sentinel.kept"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            Environment.SetEnvironmentVariable(conflicting, previousConflicting);
        }

        AssertEx.False(File.Exists(hookRan), $"the commit ran the pre-commit hook the {variable} config names");
        AssertEx.Equal("fixture.txt\n", await TestGit.RunAsync(fixture.Path, "ls-tree", "--name-only", "HEAD"));
    }

    /// <summary>
    ///     An operator's default ignore file (under <c>XDG_CONFIG_HOME</c>) or init template matching the fixture file
    ///     must not keep it out of the fixture commit. Run alone: the variable reaches every git this process starts.
    /// </summary>
    [Test]
    [NotInParallel]
    [Arguments("XDG_CONFIG_HOME")]
    [Arguments("GIT_TEMPLATE_DIR")]
    public async Task RunAsync_WithAnOperatorIgnoreFileOrTemplate_StagesTheFixtureFile(string variable)
    {
        using var operatorConfig = new TempDirectory("test-git-operator");
        var ignoreFile = variable == "XDG_CONFIG_HOME"
            ? operatorConfig.FilePath(Path.Combine("git", "ignore"))
            : operatorConfig.FilePath(Path.Combine("info", "exclude"));
        Directory.CreateDirectory(Path.GetDirectoryName(ignoreFile)!);
        await File.WriteAllTextAsync(ignoreFile, "*.txt\n");
        using var fixture = new TempDirectory("test-git");
        await File.WriteAllTextAsync(fixture.FilePath("fixture.txt"), "fixture");

        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, operatorConfig.Path);
        try
        {
            await TestGit.RunAsync(fixture.Path, "init", "--quiet", "--initial-branch=main", ".");
            await TestGit.RunAsync(fixture.Path, "config", "user.email", "test-git@example.invalid");
            await TestGit.RunAsync(fixture.Path, "config", "user.name", "Test Git");
            await TestGit.RunAsync(fixture.Path, "add", "fixture.txt");
            await TestGit.RunAsync(fixture.Path, "commit", "--quiet", "-m", "fixture");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }

        AssertEx.Equal("fixture.txt\n", await TestGit.RunAsync(fixture.Path, "ls-tree", "--name-only", "HEAD"));
    }

    /// <summary>
    ///     <paramref name="key" /> as plain git reads it from the scope <paramref name="variable" /> names, with the
    ///     <paramref name="conflicting" /> scope and every config override hidden from it.
    /// </summary>
    private static async Task<string> RawGitConfigAsync(string workingDirectory, string variable, string conflicting, string key)
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
        foreach (var overrideVariable in TestGitEnvironment.ConfigOverrides.Append("GIT_CONFIG_NOSYSTEM"))
        {
            startInfo.Environment.Remove(overrideVariable);
        }

        startInfo.Environment[conflicting] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add(variable == "GIT_CONFIG_GLOBAL" ? "--global" : "--system");
        startInfo.ArgumentList.Add("--get");
        startInfo.ArgumentList.Add(key);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(expected: 0, process.ExitCode, $"git config --get {key} ({variable}) failed: {await error}");
        return await output;
    }

    private static string Snapshot(string root) =>
        string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => $"{Path.GetRelativePath(root, file)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}"));
}
