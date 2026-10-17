namespace XE_Local_AI_Engine.Tests.Coder;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Client.Services.Coder.Implementation;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The coder reads inside a <see cref="CoderRepositoryScope" /> serve the bound repository, read-only and confined, instead of
///     the AgentHome workspace. The provider here has no sandbox at all, so a read that still went to it answers "no workspace".
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CoderRepositoryScopeTests : IDisposable
{
    private readonly string _outside = Directory.CreateTempSubdirectory("xe-coder-outside-").FullName;
    private readonly string _repository = Directory.CreateTempSubdirectory("xe-coder-repo-").FullName;

    public CoderRepositoryScopeTests()
    {
        Write("calc.py", "def add(a, b):\n    return a + b\n");
        Write("tests/test_calc.py", "from calc import add\n");
        Write(".git/config", "[remote \"origin\"]\n    url = https://token@example.invalid/repo.git\n");
        Write(".env", "API_KEY=coderscopesentinel\n");
        Write(".git/refs/heads/release-coderscopebranch", "0000000000000000000000000000000000000000\n");
        Write(".ssh/id_coderscopekey", "key\n");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outsidesentinel");
    }

    public void Dispose()
    {
        Directory.Delete(_repository, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    [Test]
    public async Task OutsideTheScope_TheReadsStillGoToTheAgentHomeWorkspace()
    {
        var result = await CreateReader().ListFilesAsync(new ListFilesToolRequest());

        AssertEx.Contains(result, "No project workspace is available", message: "without a scope nothing changes for chat and AgentHome.");
    }

    [Test]
    public async Task ListFiles_InTheScope_ListsTheRepositoryWithoutGitMetadataOrSecrets()
    {
        using var scope = CoderRepositoryScope.Begin(_repository);

        var result = await CreateReader().ListFilesAsync(new ListFilesToolRequest());

        AssertEx.Contains(result, "calc.py");
        AssertEx.Contains(result, "tests/test_calc.py");
        AssertEx.False(result.Contains(".git", StringComparison.Ordinal), "the checkout's git metadata is not part of the repository view.");
        AssertEx.False(result.Contains(".env", StringComparison.Ordinal), "a credential file is never listed.");
        AssertEx.False(result.Contains(_repository, StringComparison.Ordinal), "the host root never reaches the model.");
    }

    [Test]
    public async Task ReadFileAndSearch_InTheScope_ServeTheRepository()
    {
        using var scope = CoderRepositoryScope.Begin(_repository);
        var reader = CreateReader();

        AssertEx.Contains(await reader.ReadFileAsync(new ReadFileToolRequest
        {
            Path = "calc.py"
        }), "return a + b");
        AssertEx.Contains(await reader.SearchTextAsync(new SearchTextToolRequest
        {
            Pattern = "def add"
        }), "calc.py:1");
    }

    [Test]
    public async Task ReadFile_InTheScope_RefusesGitMetadataSecretsAndTraversal()
    {
        using var scope = CoderRepositoryScope.Begin(_repository);
        var reader = CreateReader();

        var git = await reader.ReadFileAsync(new ReadFileToolRequest
        {
            Path = ".git/config"
        });
        var secret = await reader.ReadFileAsync(new ReadFileToolRequest
        {
            Path = ".env"
        });
        var traversal = await reader.ReadFileAsync(new ReadFileToolRequest
        {
            Path = "../" + Path.GetFileName(_outside) + "/secret.txt"
        });

        AssertEx.False(git.Contains("token@", StringComparison.Ordinal), "a remote URL in .git/config can carry a token.");
        AssertEx.False(secret.Contains("coderscopesentinel", StringComparison.Ordinal));
        AssertEx.False(traversal.Contains("outsidesentinel", StringComparison.Ordinal), "a path may never leave the repository.");
    }

    /// <summary>A survey rooted INSIDE .git or a secret directory is refused, since suppression prunes only below the scan root.</summary>
    [Test]
    public async Task ListAndSearch_InTheScope_RefuseARootInsideGitMetadataOrASecretDirectory()
    {
        using var scope = CoderRepositoryScope.Begin(_repository);
        var reader = CreateReader();

        var git = await reader.ListFilesAsync(new ListFilesToolRequest
        {
            Path = ".git"
        });
        var refs = await reader.ListFilesAsync(new ListFilesToolRequest
        {
            Path = ".git/refs"
        });
        var secret = await reader.ListFilesAsync(new ListFilesToolRequest
        {
            Path = ".ssh"
        });
        var search = await reader.SearchTextAsync(new SearchTextToolRequest
        {
            Pattern = "0000",
            Path = ".git"
        });

        foreach (var result in new[]
                 {
                     git,
                     refs,
                     secret,
                     search
                 })
        {
            AssertEx.Contains(result, "rejected");
            AssertEx.False(result.Contains("coderscopebranch", StringComparison.Ordinal), "a branch name under .git is not part of the repository view.");
            AssertEx.False(result.Contains("coderscopekey", StringComparison.Ordinal), "nor a name inside a secret directory.");
        }
    }

    /// <summary>A null root is the AgentHome workspace, even inside an enclosing scope; disposal brings the enclosing root back.</summary>
    [Test]
    public void Begin_WithANullRoot_ClearsAnEnclosingRootUntilDisposed()
    {
        using var outer = CoderRepositoryScope.Begin(_repository);
        using (CoderRepositoryScope.Begin(null))
        {
            AssertEx.Null(CoderRepositoryScope.Root, "a step driven without a project must not read an enclosing repository.");
        }

        AssertEx.Equal(_repository, CoderRepositoryScope.Root);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ReadFile_InTheScope_RefusesASymlinkThatLeavesTheRepository()
    {
        File.CreateSymbolicLink(Path.Combine(_repository, "escape"), _outside);
        using var scope = CoderRepositoryScope.Begin(_repository);

        var result = await CreateReader().ReadFileAsync(new ReadFileToolRequest
        {
            Path = "escape/secret.txt"
        });

        AssertEx.Contains(result, "read_file rejected");
        AssertEx.False(result.Contains("outsidesentinel", StringComparison.Ordinal));
    }

    private static CoderWorkspaceReader CreateReader() =>
        new(new FakeSandboxRuntimeProvider(TimeProvider.System),
            new StubIdentityProvider(),
            new AgentHomeExecutionLeaseManager(),
            new SensitiveFileExclusionService(),
            Options.Create(new CoderOptions()),
            Options.Create(new AgentHomeOptions()));

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentHomeOwnerIdentity
            {
                OwnerUserId = "owner-repository",
                NodeId = "node-repository"
            });
    }
}
