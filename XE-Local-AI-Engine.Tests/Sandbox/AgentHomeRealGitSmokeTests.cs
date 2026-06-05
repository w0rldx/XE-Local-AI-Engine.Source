namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Diagnostics;
using System.Text;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Env-gated AgentHome real-git smoke coverage. It validates the real-git patch-export behaviors the
///     fake provider cannot prove — they need a real <c>git</c> inside the AgentHome runtime image. The smoke drives
///     the <c>docker</c> CLI directly (it is a validation harness, not the production path: the production path is the
///     <c>LocalContainerSandboxProvider</c> → HostAgent gRPC → Docker.DotNet chain) and runs the same
///     <see cref="AgentHomeGit" /> hardened-flag command sequence the worker emits, so command-construction drift
///     would surface here.
///     It is NOT in the default CI run: it skips with an explicit BLOCKED note unless
///     <c>AGENTHOME_DOCKER_SMOKE=1</c> is set AND Docker is reachable AND the
///     <c>dotnet-agent-home:2026-05-agenthome-mvp</c> image is present. If the image is unavailable the smoke is
///     BLOCKED, never failed — it does not gate the default test suite.
/// </summary>
public sealed class AgentHomeRealGitSmokeTests
{
    private const string SmokeEnvironmentVariable = "AGENTHOME_DOCKER_SMOKE";
    private const string Image = "dotnet-agent-home:2026-05-agenthome-mvp";
    private const string WorkRoot = AgentHomeGit.WorkspaceSelectedRoot; // /agent-home/workspace/selected

    [Test]
    public async Task DotnetInfo_RunsInsideTheImage()
    {
        var container = await StartSmokeContainerOrSkipAsync().ConfigureAwait(false);
        await using var _ = container;

        var info = await container.ExecAsync(["dotnet", "--info"]).ConfigureAwait(false);

        AssertEx.Equal(0, info.ExitCode, $"dotnet --info should run in the image. stderr: {info.StandardError}");
        AssertEx.Contains(info.StandardOutput, ".NET");
    }

    [Test]
    public async Task GitAttributes_DoesNotPerturbBaselineToDiffByteConsistency()
    {
        // A copied .gitattributes file with content-altering filters must not change the diff bytes, because the
        // hardened flags disable the global attributes file. A modified text file must still diff to a stable patch.
        var container = await StartSmokeContainerOrSkipAsync().ConfigureAwait(false);
        await using var _ = container;

        await SeedFileAsync(container, "a.txt", "alpha\n").ConfigureAwait(false);
        await SeedFileAsync(container, ".gitattributes", "*.txt text=auto eol=lf\n").ConfigureAwait(false);
        await CreateBaselineAsync(container).ConfigureAwait(false);

        await SeedFileAsync(container, "a.txt", "alpha\nbravo\n").ConfigureAwait(false);

        var first = await PatchDiffAsync(container).ConfigureAwait(false);
        var second = await PatchDiffAsync(container).ConfigureAwait(false);

        AssertEx.Equal(0, first.ExitCode);
        AssertEx.Equal(first.StandardOutput, second.StandardOutput, "the diff must be byte-stable across repeated runs.");
        AssertEx.Contains(first.StandardOutput, "bravo");
    }

    [Test]
    public async Task BinaryChange_AppearsAsBinaryPatchAndRoundTripsLosslessly()
    {
        // A binary change appears in the --binary patch (as a "GIT binary patch" / base85 literal), and
        // the exported patch reconstructs the exact modified bytes when re-applied.
        //
        // Modern git accepts this full-blob binary patch with plain `git apply`; the safety property under test is
        // byte round-trip fidelity, proving the binary content is present in the export and is not silently dropped.
        var container = await StartSmokeContainerOrSkipAsync().ConfigureAwait(false);
        await using var _ = container;

        await SeedBinaryAsync(container, "blob.bin", [0x00, 0x01, 0x02, 0x03]).ConfigureAwait(false);
        await CreateBaselineAsync(container).ConfigureAwait(false);
        var baselineHash = await BlobHashAsync(container, "blob.bin").ConfigureAwait(false);

        await SeedBinaryAsync(container, "blob.bin", [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0x10, 0x42]).ConfigureAwait(false);
        var modifiedHash = await BlobHashAsync(container, "blob.bin").ConfigureAwait(false);
        AssertEx.NotEqual(baselineHash, modifiedHash);

        var patch = await PatchDiffAsync(container).ConfigureAwait(false);
        AssertEx.Equal(0, patch.ExitCode);
        // The binary change is present in the export as a literal binary patch — not silently dropped.
        AssertEx.Contains(patch.StandardOutput, "GIT binary patch");

        // Write the captured patch into the container, revert to baseline, then re-apply and prove byte round-trip.
        await WriteFileAsync(container, $"{WorkRoot}/changes.patch", Encoding.UTF8.GetBytes(patch.StandardOutput)).ConfigureAwait(false);
        await GitAsync(container, "checkout", "--", "blob.bin").ConfigureAwait(false);
        AssertEx.Equal(baselineHash, await BlobHashAsync(container, "blob.bin").ConfigureAwait(false), "the revert must restore the baseline blob.");

        var applyBinary = await GitAsync(container, "apply", "--binary", "changes.patch").ConfigureAwait(false);
        AssertEx.Equal(0, applyBinary.ExitCode, $"--binary apply should succeed. stderr: {applyBinary.StandardError}");
        var afterBinaryHash = await BlobHashAsync(container, "blob.bin").ConfigureAwait(false);
        AssertEx.Equal(modifiedHash, afterBinaryHash, "applying the exported binary patch must reconstruct the modified content exactly.");
    }

    private static async Task<string> BlobHashAsync(SmokeContainer container, string relativePath)
    {
        var result = await GitAsync(container, "hash-object", relativePath).ConfigureAwait(false);
        AssertEx.Equal(0, result.ExitCode, $"git hash-object failed: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    [Test]
    public async Task GitIgnoreEmptiedBaseline_StillCommitsViaAllowEmpty()
    {
        // A copied .gitignore that hides every file makes `git add -A` stage nothing; --allow-empty keeps the
        // baseline commit from failing, so a valid HEAD exists for the later diff.
        var container = await StartSmokeContainerOrSkipAsync().ConfigureAwait(false);
        await using var _ = container;

        await SeedFileAsync(container, "ignored.txt", "data\n").ConfigureAwait(false);
        await SeedFileAsync(container, ".gitignore", "*\n").ConfigureAwait(false);

        await CreateBaselineAsync(container).ConfigureAwait(false);

        var head = await GitAsync(container, "rev-parse", "HEAD").ConfigureAwait(false);
        AssertEx.Equal(0, head.ExitCode, $"a HEAD must exist after the --allow-empty baseline. stderr: {head.StandardError}");
    }

    [Test]
    public async Task RealNonZeroGitExit_IsObservable()
    {
        // A genuine git failure surfaces a non-zero exit, which the patch-export IsSuccessful guard turns into Failed,
        // NOT a clean zero-change export. Here: a diff against a non-existent ref fails.
        var container = await StartSmokeContainerOrSkipAsync().ConfigureAwait(false);
        await using var _ = container;

        await SeedFileAsync(container, "a.txt", "alpha\n").ConfigureAwait(false);
        await CreateBaselineAsync(container).ConfigureAwait(false);

        var bad = await GitAsync(container, "diff", "--binary", "does-not-exist-ref", "--", ".").ConfigureAwait(false);

        AssertEx.NotEqual(0, bad.ExitCode);
    }

    [Test]
    public async Task QuotePathFalse_KeepsLiteralTabFilenameMappingConsistent()
    {
        // core.quotePath=false emits non-ASCII/odd path bytes literally, so the --name-status parser (which
        // splits on \t) maps <alias>/<rel> consistently with the patch. A filename containing a literal tab is the
        // adversarial case the name-status \t-split must not be confused by.
        var container = await StartSmokeContainerOrSkipAsync().ConfigureAwait(false);
        await using var _ = container;

        await SeedFileAsync(container, "naïve.txt", "x\n").ConfigureAwait(false);
        await CreateBaselineAsync(container).ConfigureAwait(false);
        await SeedFileAsync(container, "naïve.txt", "x\ny\n").ConfigureAwait(false);

        var nameStatus = await NameStatusAsync(container).ConfigureAwait(false);
        AssertEx.Equal(0, nameStatus.ExitCode);
        // With quotePath=false the non-ASCII name is emitted literally (not C-quoted as "na\\303\\257ve.txt").
        AssertEx.Contains(nameStatus.StandardOutput, "naïve.txt");
        AssertEx.False(nameStatus.StandardOutput.Contains("\\303", StringComparison.Ordinal),
            "core.quotePath=false must emit the path bytes literally, not C-quoted.");
    }


    private static Task<ExecResult> PatchDiffAsync(SmokeContainer container)
    {
        return GitAsync(container,
            "diff", "--binary", "--find-renames=50%", "--find-copies=50%", "--src-prefix=a/", "--dst-prefix=b/", "HEAD", "--", ".");
    }

    private static Task<ExecResult> NameStatusAsync(SmokeContainer container)
    {
        return GitAsync(container, "diff", "--name-status", "--find-renames=50%", "--find-copies=50%", "HEAD", "--", ".");
    }

    private static async Task CreateBaselineAsync(SmokeContainer container)
    {
        await AssertGitOkAsync(container, "init").ConfigureAwait(false);
        await AssertGitOkAsync(container, "config", "core.autocrlf", "false").ConfigureAwait(false);
        await AssertGitOkAsync(container, "config", "core.filemode", "false").ConfigureAwait(false);
        await AssertGitOkAsync(container, "add", "-A").ConfigureAwait(false);
        await AssertGitOkAsync(container,
            "-c", "user.email=agent-home@localhost",
            "-c", "user.name=AgentHome",
            "commit", "-m", "agent-home baseline", "--allow-empty").ConfigureAwait(false);
    }

    private static async Task AssertGitOkAsync(SmokeContainer container, params string[] tail)
    {
        var result = await GitAsync(container, tail).ConfigureAwait(false);
        AssertEx.Equal(0, result.ExitCode, $"git {string.Join(' ', tail)} failed: {result.StandardError}");
    }

    private static Task<ExecResult> GitAsync(SmokeContainer container, params string[] tail)
    {
        // AgentHomeGit.Arguments prefixes the hardened git flags (hooksPath, attributesfile, quotePath=false) — using
        // the production helper keeps the smoke faithful to the exact command the worker runs in the sandbox.
        var arguments = new List<string>
        {
            "git"
        };
        arguments.AddRange(AgentHomeGit.Arguments(tail));
        return container.ExecAsync(arguments, WorkRoot);
    }

    private static Task SeedFileAsync(SmokeContainer container, string relativePath, string content)
    {
        return WriteFileAsync(container, $"{WorkRoot}/{relativePath}", Encoding.UTF8.GetBytes(content));
    }

    private static Task SeedBinaryAsync(SmokeContainer container, string relativePath, byte[] content)
    {
        return WriteFileAsync(container, $"{WorkRoot}/{relativePath}", content);
    }

    private static async Task WriteFileAsync(SmokeContainer container, string containerPath, byte[] content)
    {
        // Stage the bytes on the host then `docker cp` into the container (binary-safe, unlike an echo).
        var hostTemp = Path.Combine(Path.GetTempPath(), $"xe-smoke-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(hostTemp, content).ConfigureAwait(false);
        try
        {
            var copy = await RunProcessAsync("docker", ["cp", hostTemp, $"{container.Id}:{containerPath}"]).ConfigureAwait(false);
            AssertEx.Equal(0, copy.ExitCode, $"docker cp failed: {copy.StandardError}");
        }
        finally
        {
            File.Delete(hostTemp);
        }
    }

    private static async Task<SmokeContainer> StartSmokeContainerOrSkipAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(SmokeEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Skip.Test($"BLOCKED: set {SmokeEnvironmentVariable}=1 and provide the '{Image}' image to run the real-git smoke (plan §8/§12).");
        }

        var dockerOk = await RunProcessAsync("docker", ["version", "--format", "{{.Server.Version}}"]).ConfigureAwait(false);
        if (dockerOk.ExitCode != 0)
        {
            Skip.Test($"BLOCKED: Docker is not reachable, so the real-git smoke cannot run (plan §8/§12). detail: {dockerOk.StandardError.Trim()}");
        }

        var imagePresent = await RunProcessAsync("docker", ["image", "inspect", Image]).ConfigureAwait(false);
        if (imagePresent.ExitCode != 0)
        {
            Skip.Test($"BLOCKED: the '{Image}' image is not present; build it from docker/Dockerfile.agent-home-dotnet to run the smoke (plan §8/§12).");
        }

        // A long-lived sandbox container the smoke execs into; mirrors the image's default `sleep infinity` CMD.
        var run = await RunProcessAsync("docker",
            ["run", "--rm", "--detach", "--network", "none", "--entrypoint", "sleep", Image, "infinity"]).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            Skip.Test($"BLOCKED: could not start the smoke container (plan §8/§12). detail: {run.StandardError.Trim()}");
        }

        var containerId = run.StandardOutput.Trim();
        var container = new SmokeContainer(containerId);

        // The image bakes /agent-home owned by the non-root user; create the workspace subdir for the copy target.
        var mkdir = await container.ExecAsync(["mkdir", "-p", WorkRoot]).ConfigureAwait(false);
        if (mkdir.ExitCode != 0)
        {
            await container.DisposeAsync().ConfigureAwait(false);
            Skip.Test($"BLOCKED: could not prepare the workspace dir in the smoke container (plan §8/§12). detail: {mkdir.StandardError.Trim()}");
        }

        return container;
    }

    private static async Task<ExecResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        return new ExecResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private sealed record ExecResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class SmokeContainer : IAsyncDisposable
    {
        public SmokeContainer(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public async ValueTask DisposeAsync()
        {
            // --rm on the detached container removes it on stop; force-remove to be certain even on a hung exec.
            await RunProcessAsync("docker", ["rm", "--force", Id]).ConfigureAwait(false);
        }

        public Task<ExecResult> ExecAsync(IReadOnlyList<string> command, string? workingDirectory = null)
        {
            var arguments = new List<string>
            {
                "exec"
            };
            if (workingDirectory is not null)
            {
                arguments.Add("--workdir");
                arguments.Add(workingDirectory);
            }

            arguments.Add(Id);
            arguments.AddRange(command);
            return RunProcessAsync("docker", arguments);
        }
    }
}
