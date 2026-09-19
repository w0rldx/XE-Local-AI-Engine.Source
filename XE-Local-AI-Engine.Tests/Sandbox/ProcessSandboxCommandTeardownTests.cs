namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A command must leave NO surviving descendant when <c>ExecuteAsync</c> returns — on the SUCCESS path, not only
///     on cancel/timeout/disk-cap.
///     <para>
///         The shape that matters: <c>/bin/sh -c "…&amp;"</c> returns at once with exit 0 while its backgrounded child
///         keeps running. Because an AgentHome sandbox is owner-node scoped and reused through
///         <c>CreateOrAttach</c>, such a child outlives the tool call AND the run, can keep rewriting the workspace
///         between the node's config rewrite and the node's git invocation on every later export, and — since the
///         reaper marker is deleted when the visible command returns — is invisible to the startup orphan sweep.
///         This repository has already paid for that class of bug once (<c>docs/agent-knowledge.md</c> §2).
///     </para>
///     <para>
///         The heartbeat is a file the child appends to, polled through <see cref="AssertEx.EventuallyAsync" /> — no
///         sleep decides anything. Every test kills whatever it started from a <c>finally</c>, and the class disposes
///         the provider, so a failure cannot leave a spinner behind.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ProcessSandboxCommandTeardownTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>
    ///     The three ways a shell hands a child to the background. None of them may outlive the command, under either
    ///     isolation mode — a double-forked <c>setsid</c> child is the one that escapes a naive process-group kill, so
    ///     it is named explicitly rather than covered by "a background child".
    /// </summary>
    [Test]
    [Arguments("plain", "( CMD ) &", true)]
    [Arguments("setsid-double-fork", "setsid /bin/sh -c 'CMD' &", true)]
    [Arguments("nohup", "nohup /bin/sh -c 'CMD' >/dev/null 2>&1 &", true)]
    [Arguments("plain-no-isolation", "( CMD ) &", false)]
    [Arguments("setsid-double-fork-no-isolation", "setsid /bin/sh -c 'CMD' &", false)]
    public async Task Execute_OnSuccess_LeavesNoSurvivingDescendant(string caseName, string backgroundForm, bool isolated)
    {
        SkipUnlessUsable(isolated);

        using var provider = CreateProvider();
        var handle = await CreateSandboxAsync(provider, caseName, isolated);

        var heartbeat = $"{AgentHomeGit.WorkspaceSelectedRoot}/{caseName}.hb";
        var loop = $"while true; do printf x >> {heartbeat}; sleep 0.05; done";
        var script = backgroundForm.Replace("CMD", loop, StringComparison.Ordinal) + "; echo started";

        var result = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "teardown-" + caseName,
                Executable = "/bin/sh",
                Arguments = ["-c", script],
                WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
                Timeout = TimeSpan.FromSeconds(30)
            });

        try
        {
            AssertEx.True(result.Completed, $"the launching command itself must return: {result.StandardError}");

            // The heartbeat must STOP growing. Two reads with a settle between them, both taken through the provider
            // so the isolated case is measured the same way as the plain one.
            var first = await HeartbeatLengthAsync(provider, handle, heartbeat);
            await AssertEx.EventuallyAsync(() => true, TimeSpan.FromMilliseconds(400), "settle");
            var second = await HeartbeatLengthAsync(provider, handle, heartbeat);

            AssertEx.Equal(first,
                second,
                $"a '{caseName}' background child outlived its command (isolated={isolated}): the heartbeat grew from "
                + $"{first} to {second} bytes after ExecuteAsync returned. Every completion path must tear the command's "
                + "scope / pid namespace / process group down, or a model-spawned loop survives the run and the sandbox reuse carries it into the next one.");
        }
        finally
        {
            // Belt and braces: whatever the assertion decided, nothing this test started may outlive it.
            await provider.KillAsync(handle);
        }
    }

    /// <summary>
    ///     The teardown must not break the ordinary case: a command whose own child finishes still returns that
    ///     child's work, and a non-zero exit is still reported rather than turned into a kill.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Execute_OnSuccess_StillReturnsOutputAndExitCode(bool isolated)
    {
        SkipUnlessUsable(isolated);

        using var provider = CreateProvider();
        var handle = await CreateSandboxAsync(provider, "ordinary", isolated);

        var ok = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "teardown-ordinary-ok",
                Executable = "/bin/sh",
                Arguments = ["-c", "echo hello; exit 0"],
                WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
                Timeout = TimeSpan.FromSeconds(30)
            });

        AssertEx.True(ok.Completed, ok.StandardError);
        AssertEx.Equal(expected: 0, ok.ExitCode);
        AssertEx.Contains(ok.StandardOutput, "hello", StringComparison.Ordinal);

        var failed = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "teardown-ordinary-fail",
                Executable = "/bin/sh",
                Arguments = ["-c", "echo oops >&2; exit 7"],
                WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
                Timeout = TimeSpan.FromSeconds(30)
            });

        AssertEx.True(failed.Completed, "a non-zero exit is a completed command, not a killed one");
        AssertEx.Equal(expected: 7, failed.ExitCode, "the teardown must not overwrite the child's own exit code");
        AssertEx.Contains(failed.StandardError, "oops", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static void SkipUnlessUsable(bool isolated)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("BLOCKED: these probes background POSIX shell children in the process jail and need Linux.");
        }

        if (!isolated)
        {
            return;
        }

        using var probe = CreateProvider();
        if (!probe.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            Skip.Test("BLOCKED: this host cannot deliver SandboxIsolationMode.Filesystem, so the isolated teardown cannot be measured here.");
        }
    }

    private static ProcessSandboxRuntimeProvider CreateProvider()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
    }

    private static async Task<SandboxHandle> CreateSandboxAsync(ProcessSandboxRuntimeProvider provider, string caseName, bool isolated)
    {
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner-teardown",
                NodeId = string.Create(CultureInfo.InvariantCulture, $"node-teardown-{caseName}"),
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "dotnet-agent-home",
                ManifestVersion = AgentHomeManifest.CurrentVersion
            },
            RuntimeProfile = "dotnet-agent-home",
            Isolation = isolated ? SandboxIsolationMode.Filesystem : SandboxIsolationMode.None,
            NetworkPolicy = isolated ? SandboxNetworkPolicy.None : SandboxNetworkPolicy.Unrestricted
        });

        await provider.ResetDirectoryAsync(handle, AgentHomeGit.WorkspaceSelectedRoot);
        return handle;
    }

    /// <summary>
    ///     The heartbeat file's length, read through the provider so the isolated jail is reached the same way the
    ///     plain one is. A missing file is length zero — the child may not have written yet, which is a valid state.
    /// </summary>
    private static async Task<int> HeartbeatLengthAsync(ProcessSandboxRuntimeProvider provider, SandboxHandle handle, string sandboxPath)
    {
        try
        {
            return (await provider.ReadFileAsync(handle, sandboxPath)).Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }
}
