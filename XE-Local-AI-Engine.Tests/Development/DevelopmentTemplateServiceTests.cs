namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Acceptance evidence for the materialization itself, asserted against a real Git repository on disk
///     rather than against a mock: every criterion here is a property of the produced directory, so a test that only
///     checked the service's return value would prove nothing about it.
/// </summary>
public sealed class DevelopmentTemplateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-template-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temp tree is not worth failing a green run over.
            }
        }
    }

    [Test]
    public async Task CreateFromTemplate_ProducesAStandaloneRepositoryWithNoRemoteAndItsOwnIdentity()
    {
        var template = await CreateTemplateRepositoryAsync().ConfigureAwait(false);
        var destination = Path.Combine(_root, "created", "MyProject");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var bindings = new RecordingRepositoryBindings();
        var service = CreateService(template, bindings);

        var result = await service.CreateFromTemplateAsync(TemplateId, destination, "my-project", "main").ConfigureAwait(false);

        AssertEx.Equal(destination, bindings.RegisteredHostPath, "The materialized destination must be what gets registered.");

        // A worktree's .git is a FILE pointing into its parent repository. A directory is what proves this is a
        // standalone repository whose object store the template cannot reach and vice versa.
        AssertEx.True(Directory.Exists(Path.Combine(destination, ".git")), "The materialized .git must be a directory.");
        AssertEx.False(File.Exists(Path.Combine(destination, ".git")), "The materialized .git must not be a worktree pointer file.");

        AssertEx.Equal(string.Empty, (await GitAsync(destination, "remote").ConfigureAwait(false)).Trim(),
            "Dropping .git must remove the inherited origin, so a stray push cannot land in the template.");

        // The four workspace invariants, as the engine will later evaluate them.
        AssertEx.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath((await GitAsync(destination, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Trim())));
        AssertEx.Equal(Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)), ".git"),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath((await GitAsync(destination, "rev-parse", "--git-common-dir").ConfigureAwait(false)).Trim(), destination)));

        var head = (await GitAsync(destination, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false)).Trim();
        AssertEx.Equal(head, (await GitAsync(destination, "rev-parse", "--verify", "refs/heads/main^{commit}").ConfigureAwait(false)).Trim(),
            "The base branch must resolve to the initial commit, because that is what the managed worktree is created from.");
        AssertEx.Equal("main", (await GitAsync(destination, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false)).Trim());

        AssertEx.NotEqual(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(Path.TrimEndingDirectorySeparator(Path.GetFullPath(template))),
            DevelopmentWorkspaceSecurity.RepositoryIdentityHash(Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination))),
            "A project created from a template must not share the template's repository identity.");

        // The template's history must not come with it: exactly one commit, naming the template and its commit.
        AssertEx.Equal("1", (await GitAsync(destination, "rev-list", "--count", "HEAD").ConfigureAwait(false)).Trim());
        var message = (await GitAsync(destination, "log", "-1", "--pretty=%s").ConfigureAwait(false)).Trim();
        AssertEx.True(message.StartsWith("Initial commit from template fixture-template @ ", StringComparison.Ordinal), message);
        AssertEx.True(message.EndsWith(result.TemplateCommit, StringComparison.Ordinal), message);
        AssertEx.NotEqual(result.TemplateCommit, head, "The initial commit is a new commit, not the template's.");
    }

    [Test]
    public async Task CreateFromTemplate_CarriesTheTemplatesProfileImportIntoTheCreatedRepository()
    {
        var template = await CreateTemplateRepositoryAsync().ConfigureAwait(false);
        var destination = Path.Combine(_root, "created", "WithProfile");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var service = CreateService(template, new RecordingRepositoryBindings());

        _ = await service.CreateFromTemplateAsync(TemplateId, destination, "with-profile", "main").ConfigureAwait(false);

        // The created repository carries the template's .xe-dev/profile.json, so the project-creation import path — which reads
        // it from the registered repository root at project creation — resolves the template's declared profile.
        var import = DevelopmentCommandProfileImport.TryRead(destination);
        AssertEx.NotNull(import, "The template's .xe-dev/profile.json must survive into the created repository.");
        AssertEx.Equal(DevelopmentCommandProfileCatalog.DotnetSlnx, import!.Document.ProfileId);
        AssertEx.Equal("Fixture.slnx", import.Document.BuildTarget);

        var profile = DevelopmentCommandProfileCatalog.Materialize(import.Document.ProfileId!,
            import.Document.BuildTarget,
            TemplateId.ToString(),
            import.Digest);
        AssertEx.Equal(DevelopmentCommandProfileCatalog.DotnetSlnx, profile.ProfileId);
        AssertEx.Equal(TemplateId.ToString(), profile.TemplateId);
        AssertEx.Equal(import.Digest, profile.ImportDigest);
    }

    [Test]
    public async Task CreateFromTemplate_RejectsADestinationInsideTheNodeDataDirectory()
    {
        var template = await CreateTemplateRepositoryAsync().ConfigureAwait(false);
        var service = CreateService(template, new RecordingRepositoryBindings());

        // Node data is where the engine's managed worktrees and runtime state live. A user-created project is a
        // user artifact and must not be inside it.
        var destination = Path.Combine(NodeDataRoot(), "development", "sneaky");
        var failure = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.CreateFromTemplateAsync(TemplateId, destination, "sneaky", "main")).ConfigureAwait(false);
        AssertEx.True(failure.Message.Contains("node data", StringComparison.OrdinalIgnoreCase), failure.Message);
        AssertEx.False(Directory.Exists(destination));
    }

    [Test]
    public async Task CreateFromTemplate_RejectsANonEmptyDestinationAndLeavesItUntouched()
    {
        var template = await CreateTemplateRepositoryAsync().ConfigureAwait(false);
        var service = CreateService(template, new RecordingRepositoryBindings());
        var destination = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "existing").ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.CreateFromTemplateAsync(TemplateId, destination, "occupied", "main")).ConfigureAwait(false);
        AssertEx.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")).ConfigureAwait(false));
    }

    [Test]
    public async Task CreateFromTemplate_RejectsAnUnsafeBaseBranch()
    {
        var template = await CreateTemplateRepositoryAsync().ConfigureAwait(false);
        var service = CreateService(template, new RecordingRepositoryBindings());
        var destination = Path.Combine(_root, "unsafe-branch");

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.CreateFromTemplateAsync(TemplateId, destination, "unsafe", "--upload-pack=touch")).ConfigureAwait(false);
        AssertEx.False(Directory.Exists(destination));
    }

    [Test]
    public async Task CreateFromTemplate_RemovesTheDirectoryItCreatedWhenRegistrationFails()
    {
        var template = await CreateTemplateRepositoryAsync().ConfigureAwait(false);
        var destination = Path.Combine(_root, "created", "DoomedProject");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var service = CreateService(template, new FailingRepositoryBindings());

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => service.CreateFromTemplateAsync(TemplateId, destination, "doomed", "main")).ConfigureAwait(false);

        // A half-materialized tree left behind would register as a repository on the next attempt and silently carry
        // whatever the failed run produced.
        AssertEx.False(Directory.Exists(destination), "A failed materialization must not leave its directory behind.");
    }

    private static readonly Guid TemplateId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private string NodeDataRoot() =>
        Path.Combine(_root, "nodedata");

    private DevelopmentTemplateService CreateService(string templateRoot, IDevelopmentRepositoryBindingService bindings)
    {
        Directory.CreateDirectory(NodeDataRoot());
        return new DevelopmentTemplateService(new FakeTemplateStore(TemplateId, "fixture-template", templateRoot),
            bindings,
            new FixedNodeDataDirectory(NodeDataRoot()),
            Options.Create(new DevelopmentOptions()),
            TimeProvider.System);
    }

    /// <summary>
    ///     A real Git repository standing in for a template. Deliberately not <c>XE-Framework</c>: that repository does
    ///     not restore at HEAD (NU1903 on a pinned transitive package), and this test is about clone, identity and
    ///     registration rather than about building the result.
    /// </summary>
    private async Task<string> CreateTemplateRepositoryAsync()
    {
        var templateRoot = Path.Combine(_root, "template");
        Directory.CreateDirectory(Path.Combine(templateRoot, ".xe-dev"));
        await File.WriteAllTextAsync(Path.Combine(templateRoot, "Fixture.slnx"), "<Solution />").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(templateRoot, "README.md"), "template").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(templateRoot, ".xe-dev", "profile.json"),
            """{"profileId":"dotnet-slnx","buildTarget":"Fixture.slnx"}""").ConfigureAwait(false);

        _ = await GitAsync(templateRoot, "init", "--initial-branch=main").ConfigureAwait(false);
        _ = await GitAsync(templateRoot, "config", "user.email", "template@example.invalid").ConfigureAwait(false);
        _ = await GitAsync(templateRoot, "config", "user.name", "Template Fixture").ConfigureAwait(false);
        _ = await GitAsync(templateRoot, "add", "-A", "--", ".").ConfigureAwait(false);
        _ = await GitAsync(templateRoot, "commit", "-m", "template baseline").ConfigureAwait(false);

        // A template is a living repository with real history; one more commit makes "the commit sha is the version"
        // observable rather than incidental.
        await File.WriteAllTextAsync(Path.Combine(templateRoot, "README.md"), "template v2").ConfigureAwait(false);
        _ = await GitAsync(templateRoot, "commit", "-am", "template second commit").ConfigureAwait(false);
        return templateRoot;
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
#pragma warning disable S4036 // Test-only helper; git resolves through the controlled PATH exactly as the product does.
        var startInfo = new ProcessStartInfo("git")
#pragma warning restore S4036
        {
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    private sealed class FixedNodeDataDirectory(string root) : INodeDataDirectory
    {
        public string Root { get; } = root;
    }

    private sealed class FakeTemplateStore(Guid templateId, string templateAlias, string hostPath) : IDevelopmentTemplateStore
    {
        public DevelopmentTemplateMaterializationSnapshot? Recorded { get; private set; }

        public Task<IReadOnlyList<DevelopmentTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentTemplateSnapshot>>([Snapshot()]);

        public Task<DevelopmentTemplateSnapshot> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            templateId == TemplateId
                ? Task.FromResult(Snapshot())
                : throw new KeyNotFoundException($"Development template '{templateId}' was not found.");

        public Task<DevelopmentTemplateSnapshot> AddAsync(string templateAlias, string hostPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task RecordMaterializationAsync(DevelopmentTemplateMaterializationSnapshot materialization,
            CancellationToken cancellationToken = default)
        {
            Recorded = materialization;
            return Task.CompletedTask;
        }

        public Task<DevelopmentTemplateMaterializationSnapshot?> FindMaterializationAsync(Guid selectedFolderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Recorded);

        private DevelopmentTemplateSnapshot Snapshot() =>
            new(templateId, templateAlias, hostPath, CreatedAtUtc: 0, Version: 1);
    }

    private class RecordingRepositoryBindings : IDevelopmentRepositoryBindingService
    {
        public string? RegisteredHostPath { get; private set; }

        public virtual Task<DevelopmentRepositoryReference> RegisterAsync(string displayAlias,
            string hostPath,
            CancellationToken cancellationToken = default)
        {
            RegisteredHostPath = hostPath;
            return Task.FromResult(new DevelopmentRepositoryReference(Guid.NewGuid().ToString(), displayAlias, "Available"));
        }

        public Task<IReadOnlyList<DevelopmentRepositoryReference>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DevelopmentRepositoryReference>>([]);

        public Task<DevelopmentRepositoryBinding> ResolveFolderAsync(Guid selectedFolderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentRepositoryBinding> ResolveProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentRepositoryBinding> ResolveExecutionAsync(DevelopmentExecutionSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentProjectSnapshot> ReconnectAsync(Guid projectId,
            Guid selectedFolderId,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FailingRepositoryBindings : RecordingRepositoryBindings
    {
        public override Task<DevelopmentRepositoryReference> RegisterAsync(string displayAlias,
            string hostPath,
            CancellationToken cancellationToken = default) =>
            throw new DevelopmentWorkspaceSecurityException("The selected folder must be a Git repository root.");
    }
}
