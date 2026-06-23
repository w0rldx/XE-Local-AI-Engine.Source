namespace XE_Local_AI_Engine.Tests.Coder;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Client.Services.Coder.Implementation;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behavior coverage for <see cref="CoderWorkspaceReader" /> against the REAL
///     <see cref="ProcessSandboxRuntimeProvider" /> jail (Linux). Reads go through the jail-guarded read; list/search
///     run real find/grep with arg-confinement and grep-level secret exclusion. The MEDIUM-1 gate proves an
///     absolute/<c>..</c> path arg can never read a host file outside the jail. The no-sandbox, disabled, binary,
///     traversal, and concurrency cases are pinned here too.
/// </summary>
public sealed class CoderWorkspaceReaderTests : IDisposable
{
    private const string Owner = "owner-coder";
    private const string Node = "node-coder";

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
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    public async Task ReadFile_WhenWithinRoot_ReturnsContentRelative()
    {
        using var provider = CreateProvider();
        await SeedWorkspaceFileAsync(provider, "src/Program.cs", "line1\nline2\nline3");
        var reader = CreateReader(provider);

        var result = await reader.ReadFileAsync(new ReadFileToolRequest { Path = "src/Program.cs" });

        AssertEx.Contains(result, "line1");
        AssertEx.Contains(result, "src/Program.cs");
        AssertEx.False(result.Contains("/agent-home", StringComparison.Ordinal), "no host/sandbox-absolute path may leak to the model");
        AssertEx.False(result.Contains(Path.GetTempPath(), StringComparison.Ordinal), "no host path may leak to the model");
    }

    [Test]
    public async Task ReadFile_WhenPathTraversal_ReturnsRejection()
    {
        using var provider = CreateProvider();
        var reader = CreateReader(provider);
        // Attach the sandbox (otherwise a no-sandbox message would mask the traversal rejection).
        await CreateOrAttachAsync(provider);

        var traversal = await reader.ReadFileAsync(new ReadFileToolRequest { Path = "../../etc/passwd" });
        var absolute = await reader.ReadFileAsync(new ReadFileToolRequest { Path = "/etc/passwd" });

        AssertEx.Contains(traversal, "rejected", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(absolute, "rejected", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(traversal.Contains("root:", StringComparison.Ordinal), "the host passwd content must never be read");
    }

    [Test]
    public async Task ReadFile_WhenSymlinkEscapesRoot_ReturnsRejection()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await CreateOrAttachAsync(provider);
        using var escapeTarget = new TempDir(_tempPaths);
        await File.WriteAllTextAsync(Path.Combine(escapeTarget.Path, "secret.txt"), "OUTSIDE-THE-JAIL");

        // Plant a jail-side symlink that escapes the workspace, then try to read through it.
        await RunShellInJailAsync(provider, handle,
            $"mkdir -p agent-home/workspace/selected && ln -s {ShellQuote(Path.Combine(escapeTarget.Path, "secret.txt"))} agent-home/workspace/selected/leak");

        var reader = CreateReader(provider);
        var result = await reader.ReadFileAsync(new ReadFileToolRequest { Path = "leak" });

        AssertEx.Contains(result, "rejected", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(result.Contains("OUTSIDE-THE-JAIL", StringComparison.Ordinal), "an escaping symlink must never be followed");
    }

    [Test]
    public async Task ReadFile_WhenBinary_RefusesAndWhenLarge_Truncates()
    {
        using var provider = CreateProvider();
        await SeedWorkspaceFileAsync(provider, "bin/data.bin", "abc\0def");
        var oversize = string.Join('\n', Enumerable.Range(1, 5000).Select(i => $"line {i}"));
        await SeedWorkspaceFileAsync(provider, "src/big.txt", oversize);
        var reader = CreateReader(provider);

        var binary = await reader.ReadFileAsync(new ReadFileToolRequest { Path = "bin/data.bin" });
        var large = await reader.ReadFileAsync(new ReadFileToolRequest { Path = "src/big.txt" });

        AssertEx.Contains(binary, "binary", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(large, "truncated", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(large.Contains("line 5000", StringComparison.Ordinal), "the default line cap must truncate before the last line");
    }

    [Test]
    public async Task ReadFile_WhenLineRange_HonorsRange()
    {
        using var provider = CreateProvider();
        await SeedWorkspaceFileAsync(provider, "src/a.txt", "alpha\nbeta\ngamma\ndelta");
        var reader = CreateReader(provider);

        var result = await reader.ReadFileAsync(new ReadFileToolRequest { Path = "src/a.txt", StartLine = 2, EndLine = 3 });

        AssertEx.Contains(result, "beta");
        AssertEx.Contains(result, "gamma");
        AssertEx.False(result.Contains("alpha", StringComparison.Ordinal), "a line range must exclude lines before startLine");
        AssertEx.False(result.Contains("delta", StringComparison.Ordinal), "a line range must exclude lines after endLine");
    }

    [Test]
    public async Task ListFiles_ExcludesSecretsAndHeavyDirs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await CreateOrAttachAsync(provider);
        await RunShellInJailAsync(provider, handle,
            "mkdir -p agent-home/workspace/selected/src agent-home/workspace/selected/node_modules agent-home/workspace/selected/bin "
            + "&& echo code > agent-home/workspace/selected/src/Program.cs "
            + "&& echo secret > agent-home/workspace/selected/.env "
            + "&& echo creds > agent-home/workspace/selected/secrets.json "
            + "&& echo dep > agent-home/workspace/selected/node_modules/lib.js "
            + "&& echo obj > agent-home/workspace/selected/bin/out.dll");
        var reader = CreateReader(provider);

        var result = await reader.ListFilesAsync(new ListFilesToolRequest());

        AssertEx.Contains(result, "src/Program.cs");
        AssertEx.False(result.Contains(".env", StringComparison.Ordinal), ".env must be excluded from the listing");
        AssertEx.False(result.Contains("secrets.json", StringComparison.Ordinal), "secrets.json must be excluded");
        AssertEx.False(result.Contains("node_modules", StringComparison.Ordinal), "node_modules must be excluded");
        AssertEx.False(result.Contains("bin/out.dll", StringComparison.Ordinal), "bin must be excluded");
    }

    [Test]
    public async Task SearchText_ReturnsRelativeLineMatches_CappedAtMax()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await CreateOrAttachAsync(provider);
        await RunShellInJailAsync(provider, handle,
            "mkdir -p agent-home/workspace/selected/src "
            + "&& printf 'needle one\\nother\\nneedle two\\n' > agent-home/workspace/selected/src/a.txt");
        var reader = CreateReader(provider);

        var result = await reader.SearchTextAsync(new SearchTextToolRequest { Pattern = "needle", MaxMatches = 1 });

        AssertEx.Contains(result, "src/a.txt:");
        // Capped at 1 match: only the first needle line appears.
        AssertEx.Equal(expected: 1, result.Split('\n').Count(line => line.Contains("needle", StringComparison.Ordinal)));
    }

    [Test]
    public async Task SearchText_SecretFileContent_NeverInOutput()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await CreateOrAttachAsync(provider);
        await RunShellInJailAsync(provider, handle,
            "mkdir -p agent-home/workspace/selected/src agent-home/workspace/selected/.git "
            + "&& echo 'APIKEY=supersecret' > agent-home/workspace/selected/.env "
            + "&& echo 'APIKEY=gitsecret' > agent-home/workspace/selected/.git/config "
            + "&& echo 'APIKEY=visible' > agent-home/workspace/selected/src/a.txt");
        var reader = CreateReader(provider);

        var result = await reader.SearchTextAsync(new SearchTextToolRequest { Pattern = "APIKEY" });

        AssertEx.Contains(result, "src/a.txt:");
        AssertEx.False(result.Contains("supersecret", StringComparison.Ordinal), ".env content must never enter search output (grep --exclude)");
        AssertEx.False(result.Contains("gitsecret", StringComparison.Ordinal), ".git content must never enter search output (grep --exclude-dir)");
    }

    [Test]
    public async Task ExecuteArg_AbsoluteOrDotDot_CannotEscapeJail_RealProvider()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // MEDIUM-1 MANDATORY GATE: a list/search whose path arg is absolute (/etc) or '..' is rejected by the guard
        // before the process launches, so a host file outside the jail is never read.
        using var provider = CreateProvider();
        var handle = await CreateOrAttachAsync(provider);
        using var escapeTarget = new TempDir(_tempPaths);
        var outsideFile = Path.Combine(escapeTarget.Path, "outside-secret.txt");
        await File.WriteAllTextAsync(outsideFile, "OUTSIDE-THE-JAIL-MEDIUM-1");
        await RunShellInJailAsync(provider, handle, "mkdir -p agent-home/workspace/selected/src && echo inside > agent-home/workspace/selected/src/a.txt");
        var reader = CreateReader(provider);

        var listAbsolute = await reader.ListFilesAsync(new ListFilesToolRequest { Path = "/etc" });
        var listTraversal = await reader.ListFilesAsync(new ListFilesToolRequest { Path = "../../../.." + escapeTarget.Path });
        var searchAbsolute = await reader.SearchTextAsync(new SearchTextToolRequest { Pattern = "OUTSIDE", Path = escapeTarget.Path });
        var searchTraversal = await reader.SearchTextAsync(new SearchTextToolRequest { Pattern = "OUTSIDE", Path = "../../../../etc" });

        AssertEx.Contains(listAbsolute, "rejected", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(listTraversal, "rejected", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(searchAbsolute, "rejected", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(searchTraversal, "rejected", StringComparison.OrdinalIgnoreCase);

        AssertEx.False(listAbsolute.Contains("OUTSIDE-THE-JAIL", StringComparison.Ordinal), "an absolute arg must never read outside the jail");
        AssertEx.False(searchAbsolute.Contains("OUTSIDE-THE-JAIL", StringComparison.Ordinal), "an absolute arg must never read outside the jail");
        AssertEx.False(searchTraversal.Contains("OUTSIDE-THE-JAIL", StringComparison.Ordinal), "a '..' arg must never read outside the jail");
    }

    [Test]
    public async Task CoderRead_WhenNoSandbox_ReturnsNoWorkspaceMessage()
    {
        using var provider = CreateProvider();
        // Do NOT create/attach a sandbox: ConnectAsync throws SandboxHandleInvalidException → no-workspace message.
        var reader = CreateReader(provider);

        var read = await reader.ReadFileAsync(new ReadFileToolRequest { Path = "src/a.txt" });
        var list = await reader.ListFilesAsync(new ListFilesToolRequest());
        var search = await reader.SearchTextAsync(new SearchTextToolRequest { Pattern = "x" });

        AssertEx.Contains(read, "select a project folder", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(list, "select a project folder", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(search, "select a project folder", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task CoderRead_DuringAgentHomeRun_DoesNotThrowBusy()
    {
        // MEDIUM-2: a coder read attaches via ConnectAsync, which never takes the AgentHome run guard, so two concurrent
        // coder reads both succeed and neither throws AgentHomeBusyException. (The fake provider's ConnectAsync mirrors
        // the real provider's lock-only attach.)
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AttachKey(provider.ProviderName),
            RuntimeProfile = "dotnet-agent-home"
        });
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = SeedHostFile(provider, "hello"),
            DestinationPath = WorkspacePathGuard.WorkspaceRoot + "/src/a.txt"
        });
        var reader = CreateReader(provider);

        var first = reader.ReadFileAsync(new ReadFileToolRequest { Path = "src/a.txt" });
        var second = reader.ReadFileAsync(new ReadFileToolRequest { Path = "src/a.txt" });
        var results = await Task.WhenAll(first, second);

        AssertEx.Contains(results[0], "hello");
        AssertEx.Contains(results[1], "hello");
    }

    // ---- harness ----

    private static ProcessSandboxRuntimeProvider CreateProvider()
    {
        var options = Options.Create(new LocalContainerOptions
        {
            MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes
        });
        return new ProcessSandboxRuntimeProvider(options, TimeProvider.System);
    }

    private static CoderWorkspaceReader CreateReader(ISandboxRuntimeProvider provider)
    {
        return new CoderWorkspaceReader(provider,
            new StubIdentityProvider(),
            new SensitiveFileExclusionService(),
            Options.Create(new CoderOptions
            {
                // A small default line cap so the truncation test trips deterministically; bytes stay generous.
                DefaultReadLineCap = 2000
            }),
            Options.Create(new AgentHomeOptions()));
    }

    private static SandboxAttachKey AttachKey(string providerName)
    {
        return new SandboxAttachKey
        {
            OwnerUserId = Owner,
            NodeId = Node,
            ProviderName = providerName,
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
    }

    private static async Task<SandboxHandle> CreateOrAttachAsync(ISandboxRuntimeProvider provider)
    {
        return await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AttachKey(provider.ProviderName),
            RuntimeProfile = "dotnet-agent-home"
        });
    }

    private async Task SeedWorkspaceFileAsync(ISandboxRuntimeProvider provider, string workspaceRelative, string content)
    {
        var handle = await CreateOrAttachAsync(provider);
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = SeedHostFile(provider, content),
            DestinationPath = WorkspacePathGuard.WorkspaceRoot + "/" + workspaceRelative
        });
    }

    private string SeedHostFile(ISandboxRuntimeProvider provider, string content)
    {
        if (provider is FakeSandboxRuntimeProvider fake)
        {
            var key = "host-" + Guid.NewGuid().ToString("N");
            fake.WriteHostFile(key, content);
            return key;
        }

        var path = Path.Combine(Path.GetTempPath(), "xe-coder-src-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        _tempPaths.Add(path);
        return path;
    }

    private static async Task RunShellInJailAsync(ProcessSandboxRuntimeProvider provider, SandboxHandle handle, string command)
    {
        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "plant-" + Guid.NewGuid().ToString("N"),
            Executable = "/bin/sh",
            Arguments = ["-c", command],
            Timeout = TimeSpan.FromSeconds(15)
        });

        AssertEx.True(result.Completed, $"the in-jail setup command must complete: {result.StandardError}");
        AssertEx.Equal(expected: 0, result.ExitCode, $"the in-jail setup command must succeed: {result.StandardError}");
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity(Owner, Node));
        }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly List<string> _tracked;

        public TempDir(List<string> tracked)
        {
            _tracked = tracked;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-coder-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            _tracked.Add(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
