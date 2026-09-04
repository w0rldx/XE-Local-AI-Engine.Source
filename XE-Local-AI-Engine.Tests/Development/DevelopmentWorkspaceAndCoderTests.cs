namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

public sealed class DevelopmentWorkspaceAndCoderTests : IDisposable
{
    /// <summary>
    ///     The profile these fixtures bind. Their repositories are a single <c>README.md</c>, so the generic profile
    ///     is the one that honestly describes them; the tests here exercise path confinement, patch guards and
    ///     evidence export, none of which depend on which build commands the profile carries. The gate's actual
    ///     build/test behaviour is covered in <c>DevelopmentValidationReviewAndApplyTests</c> against a real solution.
    /// </summary>
    private static readonly DevelopmentCommandProfile GenericProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-workspace-coder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Test]
    public void TrustAndPathGuards_RejectStaleAcknowledgementTraversalAndProtectedPaths()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = Snapshot("identity", acknowledged: false, policyVersion: null, acknowledgedAt: null);
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentTrustPolicy.EnsureCurrent(snapshot, TimeProvider.System));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentTrustPolicy.EnsureCurrent(snapshot with
        {
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion - 1,
            TrustedRepositoryAcknowledgedAtUtc = now
        }, TimeProvider.System));

        AssertEx.False(DevelopmentWorkspaceSecurity.Confine("../../outside", allowRoot: false).IsAccepted);
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".git/config", allowRoot: false).IsAccepted);
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".GIT/config", allowRoot: false).IsAccepted);
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".omx/ultragoal/goals.json", allowRoot: false).IsAccepted);
        AssertEx.True(DevelopmentWorkspaceSecurity.Confine("src/feature.cs", allowRoot: false).IsAccepted);
    }

    [Test]
    public async Task ApplyPatch_WhenRenameHeaderTargetsProtectedPath_RejectsWithoutMutation()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "protected-rename-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);
        const string patch = """
                             diff --git a/README.md b/README.md
                             similarity index 100%
                             rename from README.md
                             rename to .omx/ultragoal/VERIFIER_SENTINEL
                             """ + "\n";

        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.ApplyPatchAsync(patch));
        AssertEx.True(File.Exists(Path.Combine(session.HostWorktreePath, "README.md")));
        AssertEx.False(File.Exists(Path.Combine(session.HostWorktreePath, ".omx", "ultragoal", "VERIFIER_SENTINEL")));
    }

    [Test]
    public async Task ApplyPatch_WhenAnyExtendedHeaderTargetsProtectedPath_RejectsWholePatch()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "protected-headers-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);
        string[] patches =
        [
            "diff --git a/README.md b/README.md\nsimilarity index 100%\nrename from .git/config\nrename to README.md\n",
            "diff --git a/README.md b/README.md\nsimilarity index 100%\ncopy from README.md\ncopy to .git/config\n",
            "diff --git a/README.md b/README.md\n--- a/.git/config\n+++ b/README.md\n@@ -1 +1 @@\n-base\n+changed\n",
            "diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/.git/config\n@@ -1 +1 @@\n-base\n+changed\n"
        ];

        foreach (var patch in patches)
        {
            await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.ApplyPatchAsync(patch));
        }

        AssertEx.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(session.HostWorktreePath, "README.md")).ConfigureAwait(false));
    }

    [Test]
    public async Task ApplyPatch_WhenChangedFileExceedsWriteBound_RejectsWithoutMutation()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "patch-write-bound-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue(maxFileWriteBytes: 16));
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);
        const string patch = """
                             diff --git a/large.txt b/large.txt
                             new file mode 100644
                             index 0000000..ae52be8
                             --- /dev/null
                             +++ b/large.txt
                             @@ -0,0 +1 @@
                             +0123456789abcdefg
                             """ + "\n";

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => tools.ApplyPatchAsync(patch));
        AssertEx.False(File.Exists(Path.Combine(session.HostWorktreePath, "large.txt")));
    }

    [Test]
    public async Task ReadFile_WhenFileExceedsReadBound_FailsClosed()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "read-bound-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue(maxCommandOutputBytes: 16, maxFileWriteBytes: 64));
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);
        _ = await tools.WriteFileAsync("large.txt", "0123456789abcdefg").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<InvalidDataException>(() => tools.ReadFileAsync("large.txt"));
    }

    /// <summary>
    ///     Development Mode's read tools apply the same secret exclusion the AgentHome copy and the Coder reader already
    ///     apply. Before this, path containment plus three protected prefixes were the only guard, so a registered
    ///     repository's <c>.env</c> was a single <c>read_file</c> away from the attempt prompt — and Development Mode has
    ///     cloud role routing, so it would have left the machine.
    ///     <para>
    ///         This closes the ONE-STEP path only. Development Mode also executes the repository's own build and test
    ///         commands, and a test that prints <c>.env</c> puts those bytes into captured stdout, which reaches the same
    ///         attempt context. A hostile repository's secrets are not made safe by this; only the trivial route is shut.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ReadFile_WhenPathNamesASecretFile_RefusesWithoutReadingIt()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "read-exclusion-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);

        const string secret = "AWS_SECRET_ACCESS_KEY=devmodesentinelvalue";
        _ = await tools.WriteFileAsync(".env", secret + "\n").ConfigureAwait(false);
        _ = await tools.WriteFileAsync("deploy/node.key", secret + "\n").ConfigureAwait(false);
        _ = await tools.WriteFileAsync("certs/server.pem", secret + "\n").ConfigureAwait(false);
        _ = await tools.WriteFileAsync("secrets/nested/file.txt", secret + "\n").ConfigureAwait(false);

        foreach (var path in new[]
                 {
                     ".env",
                     "deploy/node.key",
                     "certs/server.pem"
                 })
        {
            var rejection = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.ReadFileAsync(path));
            AssertEx.False(rejection.Message.Contains("devmodesentinelvalue", StringComparison.Ordinal),
                "the refusal must not echo the content it refused to read");
        }

        // An ordinary source file under an ordinary directory is untouched — the guard must not cost the coder its job.
        _ = await tools.WriteFileAsync("src/feature.cs", "// ordinary source\n").ConfigureAwait(false);
        AssertEx.Contains(await tools.ReadFileAsync("src/feature.cs").ConfigureAwait(false), "ordinary source");
        AssertEx.Contains(await tools.ReadFileAsync("secrets/nested/file.txt").ConfigureAwait(false), "devmodesentinelvalue");
    }

    /// <summary>
    ///     The read gate uses the SECRET predicate, not the broader workspace-copy filter. Build output is skipped by
    ///     the copy because it is generated and heavy, not because it is confidential — and reading it is a primary
    ///     reason Development Mode exists: <c>obj/project.assets.json</c> is what you open when a restore fails.
    ///     Gating reads on the copy filter refused all of this while protecting nothing.
    /// </summary>
    [Test]
    public async Task ReadFile_WhenPathIsGeneratedBuildOutput_StillReadsIt()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "build-output-read-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);

        string[] buildOutputs =
        [
            "obj/project.assets.json",
            "bin/Debug/net10.0/app.deps.json",
            "node_modules/left-pad/index.js",
            "dist/main.js",
            "coverage/report.txt"
        ];

        foreach (var path in buildOutputs)
        {
            _ = await tools.WriteFileAsync(path, "restore diagnostic for " + path + "\n").ConfigureAwait(false);
            AssertEx.Contains(await tools.ReadFileAsync(path).ConfigureAwait(false), "restore diagnostic for " + path);
        }
    }

    /// <summary>
    ///     Closing the read gate alone left a two-step bypass: rename and copy are the only patch operations that move
    ///     bytes the model has never seen, so <c>rename from .env to notes.txt</c> followed by
    ///     <c>read_file("notes.txt")</c> reproduced the exact leak the gate had just closed.
    ///     <para>
    ///         Only the SOURCE side is gated. Creating <c>.env.example</c> — which matches the <c>.env.*</c> rule and
    ///         would be refused by a destination-side check — stays legal, because a creation has no secret source.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ApplyPatch_RejectsRenamingFromASecretButStillAllowsCreatingOne()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "rename-bypass-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);
        _ = await tools.WriteFileAsync(".env", "AWS_SECRET_ACCESS_KEY=renamebypasssentinel\n").ConfigureAwait(false);

        const string renameOut = """
                                 diff --git a/.env b/notes.txt
                                 similarity index 100%
                                 rename from .env
                                 rename to notes.txt
                                 """ + "\n";
        const string copyOut = """
                               diff --git a/.env b/notes.txt
                               similarity index 100%
                               copy from .env
                               copy to notes.txt
                               """ + "\n";

        foreach (var patch in new[]
                 {
                     renameOut,
                     copyOut
                 })
        {
            _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.ApplyPatchAsync(patch));
        }

        // The secret did not move, and the readable name it was aimed at does not exist.
        AssertEx.False(File.Exists(Path.Combine(session.HostWorktreePath, "notes.txt")), "the rename must not have landed");
        AssertEx.True(File.Exists(Path.Combine(session.HostWorktreePath, ".env")), "the secret must still be where it was");

        // Creating a .env.* file is ordinary work and stays legal: there is no secret source to leak.
        const string createExample = """
                                     diff --git a/.env.example b/.env.example
                                     new file mode 100644
                                     index 0000000..7898192
                                     --- /dev/null
                                     +++ b/.env.example
                                     @@ -0,0 +1 @@
                                     +AWS_SECRET_ACCESS_KEY=
                                     """ + "\n";

        _ = await tools.ApplyPatchAsync(createExample).ConfigureAwait(false);
        AssertEx.True(File.Exists(Path.Combine(session.HostWorktreePath, ".env.example")),
            "creating a .env.* file has no secret source and must remain allowed");
    }

    /// <summary>
    ///     Closing <c>read_file</c> while leaving <c>search_text</c> open would be a half-fix: a search returns the
    ///     MATCHED CONTENT, so <c>search_text("AWS_SECRET")</c> was the same one-step read of <c>.env</c> by another
    ///     name. A credential-bearing entry is PRUNED, so its bytes are never read in the first place.
    ///     <para>
    ///         This asserts the wiring end to end through a prepared workspace, which is why it needs the process
    ///         sandbox provider and therefore Linux. The survey behaviour itself is OS-independent and covered on every
    ///         host by <see cref="DevelopmentWorkspaceFileScannerTests" />.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SearchAndList_ExcludeSecretFilesButStillReturnOrdinaryContent()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("The workspace is prepared through the Linux-only process sandbox provider.");
            return;
        }

        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "search-exclusion-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);

        const string sentinel = "devmodesentinelvalue";
        _ = await tools.WriteFileAsync(".env", "AWS_SECRET_ACCESS_KEY=" + sentinel + "\n").ConfigureAwait(false);
        _ = await tools.WriteFileAsync("deploy/node.key", sentinel + "\n").ConfigureAwait(false);
        _ = await tools.WriteFileAsync("src/feature.cs", "// " + sentinel + " is referenced here\n").ConfigureAwait(false);
        _ = await tools.WriteFileAsync("obj/project.assets.json", "{ \"note\": \"" + sentinel + "\" }\n").ConfigureAwait(false);

        var matches = await tools.SearchTextAsync(sentinel, path: null).ConfigureAwait(false);
        AssertEx.False(matches.Contains(".env", StringComparison.Ordinal), "search_text must not surface .env");
        AssertEx.False(matches.Contains("node.key", StringComparison.Ordinal), "search_text must not surface node.key");
        AssertEx.Contains(matches, "src/feature.cs");

        // Build output is not a credential: it must remain searchable.
        AssertEx.Contains(matches, "obj/project.assets.json");

        var listing = await tools.ListFilesAsync(path: null).ConfigureAwait(false);
        AssertEx.False(listing.Contains(".env", StringComparison.Ordinal), "list_files must not advertise .env");
        AssertEx.False(listing.Contains("node.key", StringComparison.Ordinal), "list_files must not advertise node.key");
        AssertEx.Contains(listing, "src/feature.cs");
        AssertEx.Contains(listing, "obj/project.assets.json");

        // Git internals are dropped from the listing because Confine already refuses them as a tool argument, and a
        // fresh worktree's .git would otherwise consume the whole MaxChangedFiles budget.
        AssertEx.False(listing.Contains(".git/", StringComparison.Ordinal), "list_files must not advertise Git internals");
    }

    /// <summary>
    ///     The listing tool must not be able to spend its whole output budget on trees it is going to discard anyway.
    ///     <para>
    ///         The original defect: the listing's raw output was truncated at
    ///         <see cref="DevelopmentOptions.MaxCommandOutputBytes" /> BEFORE the suppression filter ran, and a managed
    ///         workspace is a standalone clone whose <c>.git</c> alone can outrun that cap — every surviving line then
    ///         named a suppressed path, the filter dropped all of them, and <c>list_files</c> answered with nothing
    ///         while the workspace was full of actionable files. Whether it happened depended on the order the
    ///         filesystem handed the root's entries back, which is why the suppressed trees below are created LAST.
    ///         The survey now prunes and sorts, so that ordering dependence is gone as well; the assertion is kept
    ///         because it is the end-to-end statement of the property.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ListFiles_WhenSuppressedTreesOutrunTheOutputCap_StillReturnsTheActionableFiles()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("The workspace is prepared through the Linux-only process sandbox provider.");
            return;
        }

        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "list-truncation-data");
        Directory.CreateDirectory(data);

        // Deliberately small so the suppressed bulk below outruns it many times over.
        const int OutputCap = 8 * 1024;
        var options = Options.Create(OptionsValue(maxCommandOutputBytes: OutputCap));
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);

        // The files the agent is actually there to work on, spread across several root entries.
        var actionable = new[]
        {
            "src/feature.cs",
            "docs/design.md",
            "tests/FeatureTests.cs",
            "tools/build.sh"
        };
        foreach (var relative in actionable)
        {
            var target = Path.Combine(session.HostWorktreePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "// content\n").ConfigureAwait(false);
        }

        // Git metadata on its own exceeds the cap, and so does a credential directory — the two suppression sources
        // the prune list is derived from.
        var gitBulk = FillWithBulk(Path.Combine(session.HostWorktreePath, ".git", "objects", "xe"), "development-mode-bulk-object");
        var secretBulk = FillWithBulk(Path.Combine(session.HostWorktreePath, ".aws"), "development-mode-bulk-credential");
        AssertEx.True(gitBulk > OutputCap, "the Git metadata must outrun the output cap for this to reproduce");
        AssertEx.True(secretBulk > OutputCap, "the credential directory must outrun the output cap for this to reproduce");

        var listing = await tools.ListFilesAsync(path: null).ConfigureAwait(false);

        foreach (var relative in actionable)
        {
            AssertEx.Contains(listing, relative);
        }

        AssertEx.False(listing.Contains(".git/", StringComparison.Ordinal), "list_files must not advertise Git internals");
        AssertEx.False(listing.Contains(".aws/", StringComparison.Ordinal), "list_files must not advertise a credential directory");
    }

    /// <summary>
    ///     Fills <paramref name="directory" /> with enough entries to blow past any sane command-output cap, and
    ///     returns the number of bytes their paths occupy in <c>find</c>'s output.
    /// </summary>
    private static int FillWithBulk(string directory, string namePrefix)
    {
        Directory.CreateDirectory(directory);
        var bytes = 0;
        for (var index = 0; index < 400; index++)
        {
            var name = $"{namePrefix}-{index.ToString("D4", CultureInfo.InvariantCulture)}.dat";
            File.WriteAllText(Path.Combine(directory, name), "x");
            bytes += name.Length + 1;
        }

        return bytes;
    }

    [Test]
    public async Task CoderModel_WhenModelIsUnknown_RejectsBeforeTransport()
    {
        using var chat = new ThrowingChatClient();
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected("unknown-model").Returns(false);
        var model = new DevelopmentCoderModel(chat, cloud, LocalModelResolver(), new FakeModelTrustResolver(), NullLogger<DevelopmentCoderModel>.Instance);

        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => model.RunAsync("unknown-model",
            "prompt",
            new NullWorkspaceTools(),
            maxOutputTokens: 100,
            maxToolCalls: 2));
        AssertEx.Equal(expected: 0, chat.CallCount);
    }

    /// <summary>
    ///     L1: the round is budgeted against the window the model is REALLY serving, and reserves a quarter of it.
    ///     <para>
    ///         The window used to be invented as <c>2 × maxOutputTokens</c> with <c>maxOutputTokens</c> reserved out of
    ///         it, which left exactly <c>0.7 × maxOutputTokens</c> of input budget for every model alive. Measured live
    ///         on 2026-09-02 against <c>unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL</c> served with <c>-c 65536</c>: a routed
    ///         rework round of ~24,267 estimated input tokens was refused before the provider was called, against an
    ///         "effective window" of 22,937. Nothing in that number came from the running server.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CoderModel_BudgetsTheRoundAgainstTheWindowTheRuntimeIsServing()
    {
        const int Served = 65_536;
        const int MaxOutput = 32_768;
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected("local-model").Returns(false);
        using var chat = new BudgetCapturingChatClient();
        var model = new DevelopmentCoderModel(chat,
            cloud,
            LocalModelResolver(Served, LocalModel("local-model")),
            new FakeModelTrustResolver(),
            NullLogger<DevelopmentCoderModel>.Instance);

        _ = await model.RunAsync("local-model", "prompt", new NullWorkspaceTools(), MaxOutput, maxToolCalls: 8).ConfigureAwait(false);

        var options = AssertEx.NotNull(chat.Options);
        AssertEx.True(AssertEx.NotNull(options.AdditionalProperties).TryGetValue<int>(SamplingOptionKeys.NumCtx, out var numCtx),
            "the served window has to reach the request, because that key is what the provider-round budgeter prefers over its default.");
        AssertEx.Equal(Served, numCtx);

        var budget = AssertEx.NotNull(chat.Budget);
        AssertEx.Equal(Served, budget.DefaultContextTokens, "and the scope's own default names the same window, so the two cannot disagree.");
        AssertEx.Equal(Served / 4, budget.ReservedOutputTokenFloor, "a quarter of the window is reserved for the answer, not the whole configured maximum.");
        AssertEx.Equal<int?>(Served / 4, options.MaxOutputTokens, "the reserve and the round's own output ceiling are the same number by construction.");

        // What the fix is FOR, in the terms the budgeter computes: window x 0.85 (the estimator's safety factor,
        // uncalibrated) less the reserve. 39,321 tokens of input budget against the 22,937 that refused the live round.
        AssertEx.True((int)(Served * 0.85) - budget.ReservedOutputTokenFloor > 24_267,
            "the live rework round that was refused must now fit: brief + policy + routed feedback + tool schemas.");
    }

    /// <summary>
    ///     Nothing bounds a project's maximum-tokens budget below, so zero reaches here — and a reserve clamped into
    ///     the range [1, 0] throws rather than budgeting anything.
    /// </summary>
    [Test]
    public async Task CoderModel_WithAZeroOutputBudget_StillResolvesAWindow()
    {
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected("local-model").Returns(false);
        using var chat = new BudgetCapturingChatClient();
        var model = new DevelopmentCoderModel(chat,
            cloud,
            LocalModelResolver(servedContextTokens: 65_536, LocalModel("local-model")),
            new FakeModelTrustResolver(),
            NullLogger<DevelopmentCoderModel>.Instance);

        _ = await model.RunAsync("local-model", "prompt", new NullWorkspaceTools(), maxOutputTokens: 0, maxToolCalls: 8).ConfigureAwait(false);

        var budget = AssertEx.NotNull(chat.Budget);
        AssertEx.Equal(expected: 65_536, budget.DefaultContextTokens);
        AssertEx.Equal(expected: 1, budget.ReservedOutputTokenFloor, "a zero budget reserves the floor, not a range Math.Clamp refuses.");
    }

    /// <summary>
    ///     A runtime that reports no window keeps the conservative synthetic budget — and SAYS SO, because the number it
    ///     falls back to has no connection to the process and a round refused against it is otherwise unexplainable.
    ///     No <c>num_ctx</c> override is sent: that key is what the llama.cpp reasoning-budget clamp sizes against, and
    ///     a window nothing promised would widen the clamp rather than tighten it.
    /// </summary>
    [Test]
    public async Task CoderModel_WithNoServedWindow_FallsBackAndWarnsWhichFallbackItUsed()
    {
        const int MaxOutput = 4096;
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected("local-model").Returns(false);
        using var chat = new BudgetCapturingChatClient();
        var logger = new RecordingLogger<DevelopmentCoderModel>();
        var model = new DevelopmentCoderModel(chat, cloud, LocalModelResolver(LocalModel("local-model")), new FakeModelTrustResolver(), logger);

        _ = await model.RunAsync("local-model", "prompt", new NullWorkspaceTools(), MaxOutput, maxToolCalls: 8).ConfigureAwait(false);

        var budget = AssertEx.NotNull(chat.Budget);
        AssertEx.Equal(MaxOutput * 2, budget.DefaultContextTokens, "the fallback is the pre-existing synthetic window, unchanged.");
        AssertEx.Equal(MaxOutput, budget.ReservedOutputTokenFloor, "and a fictional window does not also hand out a smaller reserve.");
        AssertEx.False(AssertEx.NotNull(chat.Options).AdditionalProperties?.ContainsKey(SamplingOptionKeys.NumCtx) ?? false,
            "a window nothing reported must not be asserted onto the request.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "no served context window"),
            $"the operator has to be told which window the attempt was budgeted against: {string.Join(" | ", logger.Entries.Select(static entry => entry.Message))}");
    }

    /// <summary>
    ///     The output budget is a WHOLE-ATTEMPT ceiling measured against a CUMULATIVE usage report, so it has to be
    ///     derived the way the input ceiling already was — per-call budget times the round count.
    ///     <para>
    ///         This test previously rejected <c>maxOutputTokens + 1</c>, which pinned the defect rather than the rule:
    ///         <c>MaxOutputTokens</c> is what the provider enforces on <em>each</em> round, while
    ///         <c>ToChatResponse().Usage</c> sums <em>every</em> round. Any tool loop whose rounds together out-talked
    ///         one round's budget was failed for exceeding a limit no call had exceeded. Reproduced live on
    ///         2026-07-31 against <c>unsloth/Ornith-1.0-9B-GGUF:Q4_K_M</c>: a coder attempt reported 33k cumulative
    ///         output tokens under a 32768 per-call budget and its completed work was discarded.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CoderModel_AcceptsCumulativeOutputAcrossRoundsAndRejectsOnlyAboveTheWholeAttemptCeiling()
    {
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected("local-model").Returns(false);
        var tools = new NullWorkspaceTools();
        var resolver = LocalModelResolver(new LocalModelDescriptor
        {
            ModelName = "local-model",
            ProviderName = "local",
            IsAvailable = true,
            SizeBytes = null,
            ModifiedAt = null,
            MaxContextTokens = 4096,
            IsToolCapable = true
        });

        // maxToolCalls 2 => at most 3 provider calls => a whole-attempt ceiling of 3 x 60.
        const int PerCall = 60;
        const int MaxToolCalls = 2;
        const long Ceiling = (MaxToolCalls + 1) * PerCall;
        AssertEx.Equal(Ceiling, DevelopmentAttemptOutputBudget.Cumulative(PerCall, MaxToolCalls + 1));

        using var exactChat = new SubmittingChatClient(inputTokens: 40_000, outputTokens: (int)Ceiling);
        var exact = new DevelopmentCoderModel(exactChat, cloud, resolver, new FakeModelTrustResolver(), NullLogger<DevelopmentCoderModel>.Instance);
        var result = await exact.RunAsync("local-model", "prompt", tools, PerCall, MaxToolCalls).ConfigureAwait(false);
        AssertEx.Equal<long?>(40_000, result.InputTokens);
        AssertEx.Equal<long?>(Ceiling, result.OutputTokens);

        // The regression the old expectation inverted: more than ONE call's budget is normal for a tool loop.
        using var multiRoundChat = new SubmittingChatClient(inputTokens: 40_000, outputTokens: PerCall + 1);
        var multiRound = new DevelopmentCoderModel(multiRoundChat, cloud, resolver, new FakeModelTrustResolver(), NullLogger<DevelopmentCoderModel>.Instance);
        var accepted = await multiRound.RunAsync("local-model", "prompt", tools, PerCall, MaxToolCalls).ConfigureAwait(false);
        AssertEx.Equal<long?>(PerCall + 1, accepted.OutputTokens);

        using var overChat = new SubmittingChatClient(inputTokens: 40_000, outputTokens: (int)Ceiling + 1);
        var over = new DevelopmentCoderModel(overChat, cloud, resolver, new FakeModelTrustResolver(), NullLogger<DevelopmentCoderModel>.Instance);
        var failure = await AssertEx.ThrowsAsync<DevelopmentAttemptEvidenceException>(() => over.RunAsync("local-model",
            "prompt",
            tools,
            PerCall,
            MaxToolCalls));
        AssertEx.Equal(DevelopmentAttemptFailureCodes.OutputTokenBudgetExceeded, failure.FailureCode);
    }

    [Test]
    [NotInParallel]
    public async Task EvidenceExport_WhenAlreadyCancelled_DoesNotStartGit()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("The executable PATH probe uses a Linux shell script.");
            return;
        }

        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var runtime = Path.Combine(_root, "cancel-runtime");
        var fakeBin = Path.Combine(_root, "fake-bin");
        var marker = Path.Combine(_root, "git-started");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(fakeBin);
        var fakeGit = Path.Combine(fakeBin, "git");
        await File.WriteAllTextAsync(fakeGit, $"#!/bin/sh\ntouch '{marker}'\nsleep 5\n").ConfigureAwait(false);
        File.SetUnixFileMode(fakeGit,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);
            using var sandbox = CreateSandbox();
            var service = new DevelopmentPatchEvidenceService(Options.Create(OptionsValue()));
            var session = new DevelopmentWorkspaceSession(Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "base",
                "identity",
                repository,
                runtime,
                new SandboxHandle
                {
                    ProviderName = "process",
                    SandboxId = Guid.NewGuid().ToString("N"),
                    AttachKey = new SandboxAttachKey
                    {
                        OwnerUserId = "development",
                        NodeId = "cancel",
                        ProviderName = "process",
                        RuntimeProfile = "development-local",
                        ManifestVersion = 1
                    },
                    CreatedAt = DateTimeOffset.UtcNow,
                    ManifestVersion = 1
                });
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync().ConfigureAwait(false);

            await AssertEx.ThrowsAsync<OperationCanceledException>(() => service.ExportAsync(session, cancelled.Token));
            AssertEx.False(File.Exists(marker), "an already-cancelled export must not start the first Git process");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Test]
    public async Task WorkspaceToolsAndEvidence_CreateDetachedReusableWorktreeWithFixedCommandsAndExactHashes()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonical));
        var protectedBefore = await RunProcessAsync(repository, "git", "rev-parse", "refs/heads/main").ConfigureAwait(false);
        EnsureSuccess(protectedBefore);
        DevelopmentWorkspaceSession first;

        using (var sandbox = CreateSandbox())
        {
            var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
            first = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
            var tools = new DevelopmentWorkspaceTools(sandbox, first, options, GenericProfile);

            _ = await tools.WriteFileAsync("src/feature.txt", "bounded change\n").ConfigureAwait(false);
            AssertEx.Equal("bounded change\n", await tools.ReadFileAsync("src/feature.txt").ConfigureAwait(false));
            await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.WriteFileAsync("../outside.txt", "blocked"));
            await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.WriteFileAsync(".git/config", "blocked"));
            await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.RunCommandAsync("model_supplied_shell"));

            var status = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus).ConfigureAwait(false);
            AssertEx.Contains(status, "src/feature.txt", StringComparison.Ordinal);
            var evidenceService = new DevelopmentPatchEvidenceService(options);
            var evidence = await evidenceService.ExportAsync(first).ConfigureAwait(false);
            AssertEx.Equal(first.BaseCommit, evidence.BaseCommit);
            AssertEx.NotNullOrEmpty(evidence.PatchHash);
            AssertEx.NotNullOrEmpty(evidence.ManifestHash);
            AssertEx.NotNullOrEmpty(evidence.SubjectHash);
            AssertEx.Contains(evidence.ChangedFiles, item => item.Path == "src/feature.txt" && item.ChangeType == "added");
            var replayEvidence = await evidenceService.ExportAsync(first).ConfigureAwait(false);
            AssertEx.Equal(evidence.PatchHash, replayEvidence.PatchHash);
            AssertEx.Equal(evidence.ManifestHash, replayEvidence.ManifestHash);
            AssertEx.Equal(evidence.SubjectHash, replayEvidence.SubjectHash);

            await sandbox.KillAsync(first.SandboxHandle).ConfigureAwait(false);
            AssertEx.True(Directory.Exists(first.HostWorktreePath), "killing a sandbox must preserve the managed Git worktree");
        }

        using var replacementSandbox = CreateSandbox();
        var replacementProvider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), replacementSandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var replacement = await replacementProvider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        AssertEx.Equal(first.HostWorktreePath, replacement.HostWorktreePath);
        var replacementTools = new DevelopmentWorkspaceTools(replacementSandbox, replacement, options, GenericProfile);
        AssertEx.Equal("bounded change\n", await replacementTools.ReadFileAsync("src/feature.txt").ConfigureAwait(false));

        var symbolic = await RunProcessAsync(replacement.HostWorktreePath, "git", "symbolic-ref", "--quiet", "HEAD").ConfigureAwait(false);
        AssertEx.NotEqual(notExpected: 0, symbolic.ExitCode, "the managed worktree must be detached from the protected base branch");
        var protectedAfter = await RunProcessAsync(repository, "git", "rev-parse", "refs/heads/main").ConfigureAwait(false);
        EnsureSuccess(protectedAfter);
        AssertEx.Equal(protectedBefore.StandardOutput.Trim(), protectedAfter.StandardOutput.Trim());
    }

    /// <summary>
    ///     The other half of the reuse rule above: the same task gets the same worktree, and two TASKS of one project
    ///     get two — the provider partitions on <c>(ProjectId, TaskId)</c>, never on the project alone.
    ///     <para>
    ///         This is what makes a decomposed feature safe to implement in parallel (C3): several materialized children
    ///         live in one Development project by design, so a project-keyed workspace would have them editing one
    ///         checkout and overwriting each other's patches, with the apply gate hashing whichever subject won. The
    ///         assertion is therefore not that the two paths differ as strings but that a file written in one is absent
    ///         from the other, and that the sandbox bound to each is a different sandbox.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WorkspaceProvider_GivesTwoTasksInOneProjectSeparateWorkspaces()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "sibling-tasks-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository));

        // Two tasks of ONE project: everything the provider is given is shared except the task and its attempt, which is
        // exactly the shape a materialized fan-out hands it.
        var first = Snapshot(identity);
        var second = first with
        {
            TaskId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid()
        };

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var firstSession = await provider.PrepareAsync(first, Binding(first, repository)).ConfigureAwait(false);
        var secondSession = await provider.PrepareAsync(second, Binding(second, repository)).ConfigureAwait(false);

        AssertEx.NotEqual(firstSession.HostWorktreePath, secondSession.HostWorktreePath, "two tasks of one project must not share a worktree");
        AssertEx.Equal(AssertEx.NotNull(Path.GetDirectoryName(firstSession.HostWorktreePath)),
            Path.GetDirectoryName(secondSession.HostWorktreePath),
            "and they are siblings under the one project directory: the task is the partition, not the project");
        AssertEx.NotEqual(firstSession.RuntimePath, secondSession.RuntimePath, "the runtime directory carries the same isolation as the worktree");
        AssertEx.NotEqual(firstSession.SandboxHandle.SandboxId,
            secondSession.SandboxHandle.SandboxId,
            "the attach key names the task, so neither task can attach to the other's sandbox");

        // Not just differently named: genuinely separate trees.
        var tools = new DevelopmentWorkspaceTools(sandbox, firstSession, options, GenericProfile);
        _ = await tools.WriteFileAsync("src/feature.txt", "the first task's work\n").ConfigureAwait(false);
        AssertEx.True(File.Exists(Path.Combine(firstSession.HostWorktreePath, "src", "feature.txt")));
        AssertEx.False(File.Exists(Path.Combine(secondSession.HostWorktreePath, "src", "feature.txt")),
            "a sibling task must not see, or be able to overwrite, work that is not its own");
    }

    /// <summary>
    ///     The managed workspace is an engine-owned standalone clone, not a linked worktree.
    ///     Every assertion here is a property the container boundary depends on, and each one fails a different way of
    ///     getting the clone wrong.
    /// </summary>
    [Test]
    public async Task WorkspaceProvider_CreatesStandaloneCloneWithNoRemoteNoSharedObjectsAndDetachedHead()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);

        // Extra commits so shallowness is observable: a source repository with a single commit would produce a
        // one-commit workspace either way, which would let the silently-ignored --depth trap pass this test.
        for (var index = 0; index < 2; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), $"base {index}\n").ConfigureAwait(false);
            EnsureSuccess(await RunProcessAsync(repository, "git", "add", "README.md").ConfigureAwait(false));
            EnsureSuccess(await RunProcessAsync(repository, "git", "commit", "-m", $"extra {index}").ConfigureAwait(false));
        }

        // A remote on the source repository: the clone inherits `origin` and it must not survive.
        EnsureSuccess(await RunProcessAsync(repository, "git", "remote", "add", "origin", "https://example.invalid/upstream.git").ConfigureAwait(false));

        var data = Path.Combine(_root, "standalone-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonical));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var workspace = session.HostWorktreePath;

        // .git is a real directory, not the pointer file a linked worktree gets — git works at all inside a bind mount.
        AssertEx.True(Directory.Exists(Path.Combine(workspace, ".git")),
            "the managed workspace must own a real .git directory, not a worktree pointer file");
        AssertEx.False(File.Exists(Path.Combine(workspace, ".git")),
            "the managed workspace .git must not be a pointer file");

        // No shared object store: nothing in the workspace names an object database it does not own.
        AssertEx.False(File.Exists(Path.Combine(workspace, ".git", "objects", "info", "alternates")),
            "the managed workspace must not share an object store with the trusted source repository");

        // Shallow: proves the file:// transport was used. Given a plain local path git ignores --depth with only a
        // warning and hardlinks the whole history, which is the exact coupling the standalone clone prevents.
        var count = await RunProcessAsync(workspace, "git", "rev-list", "--count", "HEAD").ConfigureAwait(false);
        EnsureSuccess(count);
        AssertEx.Equal("1", count.StandardOutput.Trim(),
            "the managed workspace must be a shallow clone — a full history means --depth was silently ignored");

        // No remote: the trusted source repository is not reachable by name from the workspace.
        var remotes = await RunProcessAsync(workspace, "git", "remote").ConfigureAwait(false);
        EnsureSuccess(remotes);
        AssertEx.Equal(string.Empty, remotes.StandardOutput.Trim(),
            "the managed workspace must not inherit any remote back to the trusted source repository");

        // Detached at the recorded base commit.
        var symbolic = await RunProcessAsync(workspace, "git", "symbolic-ref", "--quiet", "HEAD").ConfigureAwait(false);
        AssertEx.NotEqual(notExpected: 0, symbolic.ExitCode, "a clone leaves HEAD attached; the managed workspace must be detached");
        var head = await RunProcessAsync(workspace, "git", "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);
        EnsureSuccess(head);
        AssertEx.Equal(session.BaseCommit, head.StandardOutput.Trim());

        // The common Git directory resolves INSIDE the workspace and is not the trusted source's — the inverted
        // meaning of the --git-common-dir check for a standalone clone.
        var commonDirectory = await RunProcessAsync(workspace, "git", "rev-parse", "--git-common-dir").ConfigureAwait(false);
        EnsureSuccess(commonDirectory);
        var resolvedCommon = Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonDirectory.StandardOutput.Trim(), workspace));
        AssertEx.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(workspace, ".git"))), resolvedCommon);
        var trustedCommon = await RunProcessAsync(repository, "git", "rev-parse", "--git-common-dir").ConfigureAwait(false);
        EnsureSuccess(trustedCommon);
        AssertEx.NotEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedCommon.StandardOutput.Trim(), repository)), resolvedCommon);

        // The source repository keeps no administrative record of the workspace, which a linked worktree would have
        // left behind in .git/worktrees and which nothing in this codebase ever pruned.
        AssertEx.False(Directory.Exists(Path.Combine(repository, ".git", "worktrees")),
            "a standalone clone must leave no worktree administrative state in the trusted source repository");
    }

    /// <summary>
    ///     Repository-local Git configuration must not be able to turn a host-side git invocation into host-side
    ///     command execution.
    ///     <para>
    ///         <c>core.fsmonitor</c> is executed as a shell command on the first index refresh, and
    ///         <see cref="DevelopmentPatchEvidenceService" /> runs <c>reset</c> and <c>add -A</c> <em>on the host</em>
    ///         against the workspace. The workspace <c>.git/config</c> is writable from inside the container,
    ///         so without the pinned <c>-c core.fsmonitor=</c> this is a container-to-host escape. The include chain is
    ///         covered too: a command-line <c>-c</c> outranks configuration reached through <c>include.path</c>.
    ///     </para>
    /// </summary>
    [Test]
    public async Task EvidenceExport_WhenWorkspaceConfigPlantsFsmonitor_DoesNotExecuteItOnTheHost()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("The planted fsmonitor payload is a POSIX shell command.");
            return;
        }

        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "fsmonitor-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);
        _ = await tools.WriteFileAsync("src/feature.txt", "bounded change\n").ConfigureAwait(false);

        var directMarker = Path.Combine(_root, "fsmonitor-direct");
        var includedMarker = Path.Combine(_root, "fsmonitor-included");

        // Directly in the workspace's own config, exactly as a container-side write would land it.
        EnsureSuccess(await RunProcessAsync(session.HostWorktreePath,
            "git",
            "config",
            "core.fsmonitor",
            $"touch '{directMarker}'; false").ConfigureAwait(false));

        var evidence = new DevelopmentPatchEvidenceService(options);
        _ = await evidence.ExportAsync(session).ConfigureAwait(false);
        AssertEx.False(File.Exists(directMarker),
            "a repository-local core.fsmonitor must not execute during host-side evidence export");

        // The export now also REWRITES the workspace config to a minimal one before running git, so the planted key is
        // already gone by this point. `git config --unset` on an absent key exits 5, so the tear-down step is best
        // effort rather than EnsureSuccess'd — the property under test is unchanged, only the fixture's assumption that
        // the file survives the export.
        _ = await RunProcessAsync(session.HostWorktreePath, "git", "config", "--unset", "core.fsmonitor").ConfigureAwait(false);
        AssertEx.False((await File.ReadAllTextAsync(Path.Combine(session.HostWorktreePath, ".git", "config")).ConfigureAwait(false))
            .Contains("fsmonitor", StringComparison.OrdinalIgnoreCase),
            "the export must leave no fsmonitor definition behind in the workspace config");
        await File.WriteAllTextAsync(Path.Combine(session.HostWorktreePath, ".git", "included.config"),
            $"[core]\n\tfsmonitor = \"touch '{includedMarker}'; false\"\n").ConfigureAwait(false);
        EnsureSuccess(await RunProcessAsync(session.HostWorktreePath, "git", "config", "include.path", "included.config").ConfigureAwait(false));

        _ = await evidence.ExportAsync(session).ConfigureAwait(false);
        AssertEx.False(File.Exists(includedMarker),
            "an include.path chain must not reintroduce an executable core.fsmonitor on the host");
    }

    [Test]
    public async Task WorkspaceProvider_RejectsPreservedWorktreeWhoseHeadNoLongerMatchesPersistedBase()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "tampered-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));
        DevelopmentWorkspaceSession session;

        using (var sandbox = CreateSandbox())
        {
            var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
            session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);

            // Identity is supplied per-command because the managed workspace is now a standalone clone: `git clone`
            // does not copy the source repository's local config, so the identity the fixture set there is absent here.
            // Without this the commit fails for the wrong reason and the test would pass vacuously.
            EnsureSuccess(await RunProcessAsync(session.HostWorktreePath,
                "git",
                "-c",
                "user.email=development-workspace@example.invalid",
                "-c",
                "user.name=Development Workspace Test",
                "commit",
                "--allow-empty",
                "--no-gpg-sign",
                "-m",
                "unexpected-head").ConfigureAwait(false));
            await sandbox.KillAsync(session.SandboxHandle).ConfigureAwait(false);
        }

        using var replacementSandbox = CreateSandbox();
        var replacement = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), replacementSandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => replacement.PrepareAsync(snapshot, Binding(snapshot, repository)));
    }

    [Test]
    public async Task EvidenceExport_WhenExactPatchExceedsBound_FailsClosed()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "bounded-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue(maxPatchBytes: 128));
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);
        _ = await tools.WriteFileAsync("large.txt", new string('x', 1024)).ConfigureAwait(false);

        var evidence = new DevelopmentPatchEvidenceService(options);
        await AssertEx.ThrowsAsync<InvalidDataException>(() => evidence.ExportAsync(session));
    }

    [Test]
    public async Task CoderRunner_PersistsTypedExactEvidenceAndTerminalizesOnce()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "runner-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonical));
        var store = Substitute.For<IDevelopmentStore>();
        store.GetExecutionSnapshotAsync(snapshot.AttemptId, Arg.Any<CancellationToken>()).Returns(snapshot);
        store.AttachArtifactAsync(Arg.Any<DevelopmentAttachArtifactCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Operation(snapshot, call.Arg<DevelopmentAttachArtifactCommand>().ArtifactId));
        store.TerminalizeAttemptAsync(Arg.Any<DevelopmentTerminalizeAttemptCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Operation(snapshot, artifactId: null));

        var blob = Substitute.For<IDevelopmentArtifactBlobStore>();
        blob.WriteAsync(snapshot.ProjectId, Arg.Any<Guid>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var artifactId = call.ArgAt<Guid>(1);
                var content = call.ArgAt<ReadOnlyMemory<byte>>(2);
                return new DevelopmentArtifactBlobWriteResult($"{snapshot.ProjectId:N}/{artifactId:N}", "HASH-" + artifactId.ToString("N"), content.Length);
            });

        using var sandbox = CreateSandbox();
        var workspace = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var runner = new DevelopmentCoderAttemptRunner(store,
            workspace,
            sandbox,
            new DevelopmentPatchEvidenceService(options),
            blob,
            new WritingCoderModel(),
            new UnexpectedCloudContextService(),
            options);

        var result = await runner.RunAsync(snapshot.AttemptId, Binding(snapshot, repository)).ConfigureAwait(false);
        AssertEx.NotNullOrEmpty(result.SubjectHash);
        AssertEx.Contains(result.ChangedFiles, "feature.txt");
        _ = store.Received(5).AttachArtifactAsync(Arg.Any<DevelopmentAttachArtifactCommand>(), Arg.Any<CancellationToken>());
        _ = store.Received(1).AttachArtifactAsync(Arg.Is<DevelopmentAttachArtifactCommand>(command => command.Kind == Client.Persistence.Entities.DevelopmentArtifactKind.CoderSubmission),
            Arg.Any<CancellationToken>());
        _ = store.Received(1).TerminalizeAttemptAsync(Arg.Is<DevelopmentTerminalizeAttemptCommand>(command => command.Status == PersistenceDevelopmentAttemptStatus.Succeeded
                                                                                                              && command.InputTokens == 10
                                                                                                              && command.OutputTokens == 20),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     L5: a test-write refusal reaches the operator in the POLICY's own words. It used to be replaced by "violated
    ///     a workspace security policy", so a workflow node spent its whole retry budget — roughly ten minutes of real
    ///     model time, live — without ever telling anyone which rule it broke or what to change. The failure-code prefix
    ///     rides along so the workflow lane can class it as a policy refusal rather than as a provider error.
    /// </summary>
    [Test]
    public async Task CoderRunner_WhenTheTestWritePolicyRefuses_TerminalizesWithThePolicysOwnSentence()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(repository, "tests"));
        await File.WriteAllTextAsync(Path.Combine(repository, "tests", "FeatureTests.cs"), "// the test that exists at the base commit\n").ConfigureAwait(false);
        EnsureSuccess(await RunProcessAsync(repository, "git", "add", "tests/FeatureTests.cs").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "commit", "-m", "tests").ConfigureAwait(false));

        var data = Path.Combine(_root, "policy-runner-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));
        var store = Substitute.For<IDevelopmentStore>();
        store.GetExecutionSnapshotAsync(snapshot.AttemptId, Arg.Any<CancellationToken>()).Returns(snapshot);
        store.TerminalizeAttemptAsync(Arg.Any<DevelopmentTerminalizeAttemptCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Operation(snapshot, artifactId: null));

        using var sandbox = CreateSandbox();
        var workspace = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var runner = new DevelopmentCoderAttemptRunner(store,
            workspace,
            sandbox,
            new DevelopmentPatchEvidenceService(options),
            Substitute.For<IDevelopmentArtifactBlobStore>(),
            new TestDeletingCoderModel(),
            new UnexpectedCloudContextService(),
            options);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => runner.RunAsync(snapshot.AttemptId, Binding(snapshot, repository)))
                          .ConfigureAwait(false);

        _ = store.Received(1)
                 .TerminalizeAttemptAsync(Arg.Is<DevelopmentTerminalizeAttemptCommand>(command =>
                         command.Status == PersistenceDevelopmentAttemptStatus.Failed
                         && command.TerminalReason != null
                         && command.TerminalReason.Contains("test that existed at the base commit", StringComparison.Ordinal)
                         && DevelopmentAttemptEvidenceException.Names(command.TerminalReason, DevelopmentAttemptFailureCodes.WorkspacePolicyRefused)),
                     Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     FU3-1: a later attempt is told what the shared workspace already carries, and reporting a carried path it
    ///     returned to the base commit is accepted.
    ///     <para>
    ///         Live on 2026-09-04 a coder reverted a file an earlier attempt had created, listed it in changedFiles
    ///         because it had touched it, and lost the whole attempt to changed_file_manifest_mismatch. Its mental model
    ///         was "changed in this attempt"; the contract is "differs from the base commit at submission time". Naming
    ///         the carried files closes the gap in the prompt, and forgiving exactly this one direction closes it in the
    ///         check — the manifest artifact is derived from git, never from the submission.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CoderRunner_NamesTheCarriedFilesAndAcceptsOneReturnedToTheBaseCommit()
    {
        var second = new ScriptedCoderModel([("README.md", "base\n"), ("feature.txt", "implemented\n")], ["README.md", "feature.txt"]);

        await RunTwoAttemptsOnOneTaskAsync("carried-accept-data",
                  new ScriptedCoderModel([("README.md", "changed\n")], ["README.md"]),
                  second)
              .ConfigureAwait(false);

        AssertEx.Contains(second.Prompt, "Files in this shared workspace that already differ from the base commit");
        AssertEx.Contains(second.Prompt, "README.md");

        // get_diff has to SHOW the work the prompt points at. It diffed against the index until 2026-09-04, and the
        // index equals the worktree from the moment an attempt starts, so it answered "nothing" for every carried file.
        AssertEx.Contains(second.DiffAtStart, "--- a/README.md");
        AssertEx.Contains(second.DiffAtStart, "+changed");
    }

    /// <summary>
    ///     Both ends of a rename are carried. Attempt 1 renames README.md away; attempt 2 renames it back and adds a
    ///     file, so README.md matches the base commit again while the coder honestly reports having touched it. Only
    ///     the rename's SOURCE path makes that submission legible, and git reports it in PreviousPath.
    /// </summary>
    [Test]
    public async Task CoderRunner_CarriesBothEndsOfARenameAnEarlierAttemptMade()
    {
        await RunTwoAttemptsOnOneTaskAsync("carried-rename-data",
                  new ScriptedCoderModel([], ["renamed.md"], Rename("README.md", "renamed.md")),
                  new ScriptedCoderModel([("feature.txt", "implemented\n")], ["README.md", "feature.txt"], Rename("renamed.md", "README.md")))
              .ConfigureAwait(false);
    }

    /// <summary>A pure rename patch, which is what git emits for a move with no content change.</summary>
    private static string Rename(string from, string to) =>
        $"diff --git a/{from} b/{to}\nsimilarity index 100%\nrename from {from}\nrename to {to}\n";

    /// <summary>A path this task never changed is still over-reporting, whichever attempt claims it.</summary>
    [Test]
    public async Task CoderRunner_StillRefusesAPathThatNeverDifferedFromTheBaseCommit()
    {
        var exception = await AssertEx.ThrowsAsync<DevelopmentAttemptEvidenceException>(() =>
                                  RunTwoAttemptsOnOneTaskAsync("carried-overreport-data",
                                      new ScriptedCoderModel([("feature.txt", "implemented\n")], ["feature.txt"]),
                                      new ScriptedCoderModel([("second.txt", "more\n")], ["feature.txt", "second.txt", "README.md"])))
                              .ConfigureAwait(false);

        AssertEx.Equal(DevelopmentAttemptFailureCodes.ChangedFileManifestMismatch, exception.FailureCode);
        AssertEx.Contains(exception.OperatorReason, "Submitted but not changed: README.md");
    }

    /// <summary>Under-reporting stays fatal: that is the direction in which a change escapes the review it needs.</summary>
    [Test]
    public async Task CoderRunner_StillRefusesAnAttemptThatOmitsAFileTheWorkspaceCarries()
    {
        var exception = await AssertEx.ThrowsAsync<DevelopmentAttemptEvidenceException>(() =>
                                  RunTwoAttemptsOnOneTaskAsync("carried-underreport-data",
                                      new ScriptedCoderModel([("feature.txt", "implemented\n")], ["feature.txt"]),
                                      new ScriptedCoderModel([("second.txt", "more\n")], ["second.txt"])))
                              .ConfigureAwait(false);

        AssertEx.Equal(DevelopmentAttemptFailureCodes.ChangedFileManifestMismatch, exception.FailureCode);
        AssertEx.Contains(exception.OperatorReason, "Changed but not submitted: feature.txt");
    }

    /// <summary>
    ///     Two coder attempts on ONE task, which is what makes them share one workspace: the provider keys it by
    ///     project and task, so only the attempt id differs between the two snapshots.
    /// </summary>
    private async Task RunTwoAttemptsOnOneTaskAsync(string dataDirectoryName, ScriptedCoderModel first, ScriptedCoderModel second)
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, dataDirectoryName);
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var firstAttempt = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));
        var secondAttempt = firstAttempt with
        {
            AttemptId = Guid.NewGuid()
        };

        var store = Substitute.For<IDevelopmentStore>();
        store.GetExecutionSnapshotAsync(firstAttempt.AttemptId, Arg.Any<CancellationToken>()).Returns(firstAttempt);
        store.GetExecutionSnapshotAsync(secondAttempt.AttemptId, Arg.Any<CancellationToken>()).Returns(secondAttempt);
        store.AttachArtifactAsync(Arg.Any<DevelopmentAttachArtifactCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Operation(firstAttempt, call.Arg<DevelopmentAttachArtifactCommand>().ArtifactId));
        store.TerminalizeAttemptAsync(Arg.Any<DevelopmentTerminalizeAttemptCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Operation(firstAttempt, artifactId: null));

        var blob = Substitute.For<IDevelopmentArtifactBlobStore>();
        blob.WriteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => new DevelopmentArtifactBlobWriteResult($"{call.ArgAt<Guid>(0):N}/{call.ArgAt<Guid>(1):N}",
                "HASH-" + call.ArgAt<Guid>(1).ToString("N"),
                call.ArgAt<ReadOnlyMemory<byte>>(2).Length));

        using var sandbox = CreateSandbox();
        var workspace = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var binding = Binding(firstAttempt, repository);
        DevelopmentCoderAttemptRunner Runner(IDevelopmentCoderModel model) =>
            new(store, workspace, sandbox, new DevelopmentPatchEvidenceService(options), blob, model, new UnexpectedCloudContextService(), options);

        _ = await Runner(first).RunAsync(firstAttempt.AttemptId, binding).ConfigureAwait(false);
        _ = await Runner(second).RunAsync(secondAttempt.AttemptId, binding).ConfigureAwait(false);
    }

    private static DevelopmentOptions OptionsValue(int maxPatchBytes = 1024 * 1024,
        int maxFileWriteBytes = 1024 * 1024,
        int maxCommandOutputBytes = 256 * 1024) =>
        new()
        {
            Enabled = true,
            MaxArtifactBytes = 2 * 1024 * 1024,
            MaxPatchBytes = maxPatchBytes,
            MaxFileWriteBytes = maxFileWriteBytes,
            MaxCommandOutputBytes = maxCommandOutputBytes,
            MaxChangedFiles = 32,
            MaxToolCalls = 16,
            MaxAttemptDurationSeconds = 60,
            MaxOutputTokens = 2048
        };

    /// <summary>
    ///     MSBuild and NuGet resolve <c>Directory.Build.props</c>, <c>Directory.Build.targets</c> and
    ///     <c>Directory.Packages.props</c> by walking UP from the project until the first hit, with no upper bound. The
    ///     managed workspace therefore inherits whatever sits above the node's data directory unless something stops
    ///     the walk.
    ///     <para>
    ///         Reproduced live on 2026-07-31: running from a source checkout puts the data root inside this
    ///         repository, so a registered repository's <c>dotnet restore</c> picked up <em>this</em> repository's
    ///         Central Package Management and failed <c>NU1008</c> for a package it declares legally inline. The
    ///         validation gate was measuring the host's build configuration.
    ///     </para>
    ///     <para>
    ///         The barrier must sit one level ABOVE the workspace, never inside it: a file inside would show up as an
    ///         untracked change and land in the attempt's changed-file manifest, which is the evidence the apply gate
    ///         is built on. Both halves are asserted.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WorkspaceProvider_WritesBuildConfigurationBarrierAboveTheWorkspaceAndNotInsideIt()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "barrier-data");
        Directory.CreateDirectory(data);

        // An ancestor that would otherwise be inherited, exactly as the live defect had it.
        await File.WriteAllTextAsync(Path.Combine(data, "Directory.Packages.props"),
                      "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>")
                  .ConfigureAwait(false);

        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));
        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, Options.Create(OptionsValue()), TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);

        var workspaceParent = Path.GetDirectoryName(session.HostWorktreePath)!;
        foreach (var fileName in new[]
                 {
                     "Directory.Build.props",
                     "Directory.Build.targets",
                     "Directory.Packages.props",
                     "Directory.Solution.props"
                 })
        {
            AssertEx.True(File.Exists(Path.Combine(workspaceParent, fileName)), $"{fileName} must bound the upward search");
            AssertEx.False(File.Exists(Path.Combine(session.HostWorktreePath, fileName)),
                $"{fileName} must not be written inside the workspace, where it would become a changed file");
        }

        AssertEx.True((await File.ReadAllTextAsync(Path.Combine(workspaceParent, "Directory.Packages.props")).ConfigureAwait(false))
            .Contains("<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>", StringComparison.Ordinal));

        // The workspace stays clean: the barrier is invisible to the repository's own Git state.
        var status = await RunProcessAsync(session.HostWorktreePath, "git", "status", "--porcelain").ConfigureAwait(false);
        EnsureSuccess(status);
        AssertEx.Equal(string.Empty, status.StandardOutput.Trim());

        // A deleted barrier is restored on the next prepare rather than silently reopening the defect.
        File.Delete(Path.Combine(workspaceParent, "Directory.Packages.props"));
        _ = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        AssertEx.True(File.Exists(Path.Combine(workspaceParent, "Directory.Packages.props")));
    }

    private static DevelopmentExecutionSnapshot Snapshot(string identity,
        bool acknowledged = true,
        int? policyVersion = DevelopmentTrustPolicy.CurrentVersion,
        long? acknowledgedAt = null)
    {
        return new DevelopmentExecutionSnapshot(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            identity,
            "main",
            DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion: 1,
            acknowledged,
            policyVersion,
            acknowledgedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens: 2048,
            MaxDurationSeconds: 60,
            "Implement feature",
            "Add the bounded feature file.",
            "[\"feature.txt exists\"]",
            DevelopmentTaskStatus.InProgress,
            TaskVersion: 3,
            DevelopmentAttemptRole.Coder,
            PersistenceDevelopmentAttemptStatus.Running,
            "local-model",
            "local",
            AttemptVersion: 1,
            Encoding.UTF8.GetString(GenericProfile.ToCanonicalUtf8()));
    }

    private static DevelopmentRepositoryBinding Binding(DevelopmentExecutionSnapshot snapshot, string repository) =>
        new(snapshot.ProjectId,
            snapshot.SelectedFolderId ?? throw new InvalidOperationException("The test snapshot must have a selected folder."),
            "repository",
            repository,
            snapshot.RepositoryIdentityHash);

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        EnsureSuccess(await RunProcessAsync(repository, "git", "init", "--initial-branch=main", ".").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "config", "user.email", "development-workspace@example.invalid").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "config", "user.name", "Development Workspace Test").ConfigureAwait(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        EnsureSuccess(await RunProcessAsync(repository, "git", "add", "README.md").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "commit", "-m", "base").ConfigureAwait(false));
        return repository;
    }

    private static ProcessSandboxRuntimeProvider CreateSandbox()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
    }

    private static async Task<CommandResult> RunProcessAsync(string workingDirectory, string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void EnsureSuccess(CommandResult result)
    {
        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);
    }

    private static DevelopmentOperationResult Operation(DevelopmentExecutionSnapshot snapshot, Guid? artifactId)
    {
        return new DevelopmentOperationResult(snapshot.ProjectId,
            snapshot.TaskId,
            snapshot.AttemptId,
            artifactId,
            Guid.NewGuid(),
            DevelopmentOperationPhases.Completed,
            "ok",
            "ok",
            Version: 1,
            Sequence: 1);
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    private static ILocalModelProviderResolver LocalModelResolver(params LocalModelDescriptor[] models) =>
        LocalModelResolver(servedContextTokens: null, models);

    /// <summary>
    ///     A resolver whose runtime reports <paramref name="servedContextTokens" /> as the window it launched with, or
    ///     nothing — which is the runtime that has no fixed window, and the one that has not started yet.
    ///     <para>
    ///         The window is reported only AFTER the model has been warmed, exactly as the real runtime behaves: it
    ///         knows the launched context of a process it is running and nothing about one it has not started. Delete
    ///         the warm from the budget resolver and every served-window test falls back instead.
    ///     </para>
    /// </summary>
    private static ILocalModelProviderResolver LocalModelResolver(int? servedContextTokens, params LocalModelDescriptor[] models)
    {
        var warmed = false;
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>()).Returns(models);
        provider.When(runtime => runtime.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(_ => warmed = true);
        provider.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => servedContextTokens is { } served && warmed ? new LocalModelRuntimeInfo(served) : null);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(provider);
        return resolver;
    }

    /// <summary>One local model that is available and tool-capable, which is all these budget tests need of it.</summary>
    private static LocalModelDescriptor LocalModel(string modelName) =>
        new()
        {
            ModelName = modelName,
            ProviderName = "local",
            IsAvailable = true,
            SizeBytes = null,
            ModifiedAt = null,
            MaxContextTokens = 4096,
            IsToolCapable = true
        };

    /// <summary>A coder that takes the shortest path to green: it rewrites a test that existed at the base commit.</summary>
    private sealed class TestDeletingCoderModel : IDevelopmentCoderModel
    {
        public async Task<DevelopmentCoderModelResult> RunAsync(string modelId,
            string prompt,
            IDevelopmentWorkspaceTools tools,
            int maxOutputTokens,
            int maxToolCalls,
            DevelopmentAttemptLiveProgress? liveProgress = null,
            DevelopmentCloudRoleRoute? cloudRoute = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tools);
            _ = await tools.WriteFileAsync("tests/FeatureTests.cs", "// nothing to see here\n", cancellationToken).ConfigureAwait(false);
            return new DevelopmentCoderModelResult(new DevelopmentCoderSubmission("Made the tests pass.",
                    ["tests/FeatureTests.cs"],
                    [],
                    Notes: null),
                InputTokens: 10,
                OutputTokens: 20);
        }
    }

    /// <summary>
    ///     A coder that makes the writes it was handed and submits the changed-file list it was handed, recording the
    ///     prompt it was given. Scripting both halves is what lets a test state "this attempt reverted a file an
    ///     earlier one created and reported it" without a real model.
    /// </summary>
    private sealed class ScriptedCoderModel(IReadOnlyList<(string Path, string Content)> writes, IReadOnlyList<string> changedFiles, string? patch = null) : IDevelopmentCoderModel
    {
        public string? Prompt { get; private set; }

        /// <summary>What get_diff showed this attempt BEFORE it changed anything.</summary>
        public string? DiffAtStart { get; private set; }

        public async Task<DevelopmentCoderModelResult> RunAsync(string modelId,
            string prompt,
            IDevelopmentWorkspaceTools tools,
            int maxOutputTokens,
            int maxToolCalls,
            DevelopmentAttemptLiveProgress? liveProgress = null,
            DevelopmentCloudRoleRoute? cloudRoute = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tools);
            Prompt = prompt;
            DiffAtStart = await tools.GetDiffAsync(cancellationToken).ConfigureAwait(false);
            if (patch is not null)
            {
                _ = await tools.ApplyPatchAsync(patch, cancellationToken).ConfigureAwait(false);
            }

            foreach (var (path, content) in writes)
            {
                _ = await tools.WriteFileAsync(path, content, cancellationToken).ConfigureAwait(false);
            }

            return new DevelopmentCoderModelResult(new DevelopmentCoderSubmission("Scripted attempt.", changedFiles, [], Notes: null),
                InputTokens: 10,
                OutputTokens: 20);
        }
    }

    private sealed class WritingCoderModel : IDevelopmentCoderModel
    {
        public async Task<DevelopmentCoderModelResult> RunAsync(string modelId,
            string prompt,
            IDevelopmentWorkspaceTools tools,
            int maxOutputTokens,
            int maxToolCalls,
            DevelopmentAttemptLiveProgress? liveProgress = null,
            DevelopmentCloudRoleRoute? cloudRoute = null,
            CancellationToken cancellationToken = default)
        {
            _ = await tools.WriteFileAsync("feature.txt", "implemented\n", cancellationToken).ConfigureAwait(false);
            _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus, cancellationToken).ConfigureAwait(false);
            return new DevelopmentCoderModelResult(new DevelopmentCoderSubmission("Implemented bounded feature.",
                    ["feature.txt"],
                    [DevelopmentCommandIds.GitStatus],
                    Notes: null),
                InputTokens: 10,
                OutputTokens: 20);
        }
    }

    private sealed class UnexpectedCloudContextService : IDevelopmentCloudAttemptContextService
    {
        public Task<DevelopmentCloudAttemptContext> CreateAsync(DevelopmentExecutionSnapshot snapshot,
            IReadOnlyList<DevelopmentCloudContextExcerpt> excerpts,
            IReadOnlyList<Guid>? inputArtifactIds = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The local-only coder fixture must not build a cloud context.");
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("transport reached");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            _ = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 20,
                    TotalTokenCount = 30
                })
            ]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

        public void Dispose() { }
    }

    private sealed class NullWorkspaceTools : IDevelopmentWorkspaceTools
    {
        public IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence => [];

        /// <summary>
        ///     The coder-model tests never run a catalog command through this stub, so the generic profile — the one
        ///     code-owned profile that needs no build target — is the honest stub value.
        /// </summary>
        public DevelopmentCommandProfile Profile { get; } =
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        public Task<string> ListFilesAsync(string? path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> SearchTextAsync(string pattern, string? path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> GetDiffAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }

    /// <summary>
    ///     A coder chat client that records the round's options and the ambient provider-call budget the attempt opened
    ///     around it — the two numbers the window fix is about, read where the real budgeting client reads them.
    /// </summary>
    private sealed class BudgetCapturingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public ProviderCallBudgetOptions? Budget { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            Budget = ProviderCallBudget.Current?.Options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 1,
                    OutputTokenCount = 1,
                    TotalTokenCount = 2
                }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            var submit = AssertEx.NotNull(options?.Tools?.OfType<AIFunction>().SingleOrDefault(static tool => tool.Name == "submit_implementation"));
            _ = await submit.InvokeAsync(new AIFunctionArguments
            {
                ["summary"] = "done",
                ["changedFiles"] = Array.Empty<string>(),
                ["commandIds"] = Array.Empty<string>()
            }, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

        public void Dispose() { }
    }

    private sealed class SubmittingChatClient(long inputTokens, long outputTokens) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var submit = AssertEx.NotNull(options?.Tools?.OfType<AIFunction>()
                                                 .SingleOrDefault(static tool => tool.Name == "submit_implementation"));
            _ = await submit.InvokeAsync(new AIFunctionArguments
            {
                ["summary"] = "done",
                ["changedFiles"] = Array.Empty<string>(),
                ["commandIds"] = Array.Empty<string>()
            }, cancellationToken).ConfigureAwait(false);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens,
                    TotalTokenCount = inputTokens + outputTokens
                }
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

        public void Dispose() { }
    }
}
