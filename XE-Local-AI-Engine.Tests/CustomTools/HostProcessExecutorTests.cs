namespace XE_Local_AI_Engine.Tests.CustomTools;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Host command executor + executable guard: an interpreter/symlink executable is rejected at execution time, a
///     timeout returns a non-throwing incomplete result (tree-killed), and a secret env value is scrubbed from output.
/// </summary>
public sealed class HostProcessExecutorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "xe-customtool-" + Guid.NewGuid().ToString("N"));
    private readonly CustomToolConcurrencyLimiter _limiter = new();

    public HostProcessExecutorTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        _limiter.Dispose();
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Test]
    public async Task ExecutableGuard_RejectsInterpreter()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => HostExecutableGuard.Validate("/bin/bash"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExecutableGuard_RejectsRelativePath()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => HostExecutableGuard.Validate("git"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExecutableGuard_RejectsSymlink()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var target = Path.Combine(_scratch, "real-tool");
        await File.WriteAllTextAsync(target, "#!/bin/sh\nexit 0\n");
        var link = Path.Combine(_scratch, "link-tool");
        File.CreateSymbolicLink(link, target);

        // A regular file passes; its symlink is rejected by the O_NOFOLLOW/statx leaf check.
        HostExecutableGuard.Validate(target);
        AssertEx.Throws<CustomToolExecutionException>(() => HostExecutableGuard.Validate(link));
    }

    [Test]
    public async Task ExecuteAsync_Timeout_ReturnsNonThrowingIncompleteResult()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
        {
            return;
        }

        var script = await CreateScriptAsync("#!/bin/sh\nsleep 60\n");
        var config = $$"""{"executable":"{{script}}","argsTemplate":[],"timeoutSeconds":1,"env":[]}""";
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(MakeTool(CustomToolKind.Command, config), "{}", CancellationToken.None);

        AssertEx.Contains(result, "timed out");
    }

    [Test]
    public async Task ExecuteAsync_ScrubsSecretEnvValueFromOutput()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
        {
            return;
        }

        const string secret = "top-secret-value-9f3a";
        var script = await CreateScriptAsync("#!/bin/sh\necho \"$MY_SECRET\"\n");
        var config = $$"""{"executable":"{{script}}","argsTemplate":[],"timeoutSeconds":10,"env":[{"name":"MY_SECRET","value":"{{secret}}","isSecret":true}]}""";
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(MakeTool(CustomToolKind.Command, config), "{}", CancellationToken.None);

        AssertEx.False(result.Contains(secret, StringComparison.Ordinal), $"The secret env value must be scrubbed from tool output. Output: {result}");
        AssertEx.Contains(result, "[REDACTED]");
    }

    private HostProcessExecutor CreateExecutor()
    {
        return new HostProcessExecutor(_limiter, NullLogger<HostProcessExecutor>.Instance);
    }

    private async Task<string> CreateScriptAsync(string body)
    {
        var path = Path.Combine(_scratch, "tool-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, body);
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return path;
    }

    private static CustomToolRecord MakeTool(CustomToolKind kind, string configJson)
    {
        return new CustomToolRecord(Guid.NewGuid(),
            "custom__test_tool",
            "A test tool.",
            kind,
            CustomToolMode.Fixed,
            "[]",
            configJson,
            Enabled: true,
            Acknowledged: true,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }
}
