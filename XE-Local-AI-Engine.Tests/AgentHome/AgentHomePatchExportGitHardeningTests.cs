namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Client.Services.Coder.Implementation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;
using CategoryAttribute = TUnit.Core.CategoryAttribute;

/// <summary>
///     The patch export's git is the node's own, and it runs over the workspace the model just had <c>write_file</c>
///     and <c>run_command</c> access to, AFTER the model's turn has ended and outside the run's budgets. Git executes
///     programs named by configuration, so without a guard that is host code execution the operator never approved and
///     never sees.
///     <para>
///         This class is the negative control for that, end to end on the REAL process jail with REAL git: a scripted
///         inner run plants each payload shape the way a model actually could — its own <c>run_command</c> for the
///         config, its own <c>write_file</c> for the <c>.gitattributes</c> — and every payload would create a MARKER
///         FILE OUTSIDE the workspace. The assertion is that no marker exists after export.
///     </para>
///     <para>
///         Each payload was verified to EXECUTE against the pre-guard argument vector on git 2.53.0, so a green run
///         here is a guard working rather than a payload that never fired. The deliberate-break proof re-establishes
///         that on demand.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AgentHomePatchExportGitHardeningTests : IDisposable
{
    private const string Model = "qwen3:8b";
    private const string WorkspaceAlias = "selected-project";

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
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>
    ///     The four payloads that define a program in the repository's OWN configuration and select it from the tree.
    ///     All four executed against the pre-guard vector; all four must be inert now, and the export must still
    ///     produce the model's real edit, because a guard that broke the diff would be traded for an outage.
    /// </summary>
    [Test]
    [Arguments("textconv", "diff.pwn.textconv", "* diff=pwn", "a repository-local textconv driver selected by an in-tree .gitattributes")]
    [Arguments("clean", "filter.pwn.clean", "* filter=pwn", "a repository-local clean filter selected by an in-tree .gitattributes")]
    [Arguments("external", "diff.pwn.command", "* diff=pwn", "a repository-local external diff command")]
    // fsmonitor is additionally covered by AgentHomeGit's own -c pin; it stays here as the regression control for the
    // key that was already known to be live, so removing either control shows up.
    [Arguments("fsmonitor", "core.fsmonitor", "", "a repository-local fsmonitor hook that runs on any index refresh")]
    public async Task Export_WhenTheModelPlantedAGitConfigProgram_NeverRunsIt_AndStillExportsTheRealEdit(string caseName,
        string configKey,
        string attributes,
        string because)
    {
        SkipUnlessRealGitAndProcessJail();

        var marker = MarkerRelativePath(caseName);
        var payload = PayloadCommand(marker);

        using var fixture = CreateFixture();
        var script = new List<(string, Dictionary<string, object?>)>
        {
            // The model's own command sets the repository-local key — indistinguishable from a legitimate
            // `git config` call in a `commands.jsonl` an operator reads.
            ("run_command", new()
            {
                ["executable"] = "git",
                ["arguments"] = new[] { "config", configKey, payload }
            }),
            // …and a real edit, so the export has something to find and the test can prove the diff still works.
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" })
        };

        if (attributes.Length > 0)
        {
            // .gitattributes is NOT under .git, so the write guard does not refuse it. That is the point.
            script.Insert(index: 1, ("write_file", new() { ["path"] = $"{WorkspaceAlias}/.gitattributes", ["content"] = attributes + "\n" }));
        }

        var run = await fixture.RunAsync([.. script]);

        AssertEx.False(await MarkerExistsAsync(fixture.Provider, marker), $"the export ran {because}; the payload must never execute");
        AssertEx.Equal(expected: 1, run.Patch.ChangedFileCount > 0 ? 1 : 0,
            $"the guard must not break the diff — export reported {run.Patch.ChangedFileCount} changed file(s)");
        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     The shape that defeats a configuration rewrite outright: <c>.git</c> replaced by a GITFILE pointing at a
    ///     directory the model owns. The rewrite finds no directory, and a naive guard returns silently while git
    ///     happily reads the model's configuration from wherever the pointer leads. Verified executing against a guard
    ///     that only rewrote an existing <c>.git/config</c>.
    /// </summary>
    [Test]
    public async Task Export_WhenTheModelReplacedTheGitDirectoryWithAGitfile_RefusesToExportAtAll()
    {
        SkipUnlessRealGitAndProcessJail();

        var marker = MarkerRelativePath("gitfile");
        var payload = PayloadCommand(marker);

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/.gitattributes", ["content"] = "* diff=pwn\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }),
            ("run_command", new()
            {
                ["executable"] = "/bin/sh",
                ["arguments"] = new[]
                {
                    "-c",
                    // Copy the real repository aside, point config at the payload, then leave a gitfile behind.
                    $"cp -r .git ../evil-git && printf '[core]\\n\\trepositoryformatversion = 0\\n[diff \"pwn\"]\\n\\ttextconv = {payload}\\n' > ../evil-git/config"
                    + " && rm -rf .git && printf 'gitdir: %s\\n' \"$(cd ../evil-git && pwd)\" > .git"
                }
            }));

        AssertEx.False(await MarkerExistsAsync(fixture.Provider, marker), "a gitfile pointing at a model-owned git directory must not get the payload run");

        // Fail CLOSED, and say so: exporting from a repository the model replaced would be exporting a patch the node
        // cannot vouch for, and silently reporting "no changes" would be worse than reporting nothing.
        AssertEx.True(run.Patch.Failed, "the export must refuse a workspace whose git directory is not the one the baseline created");
        AssertEx.Equal(expected: 0, run.Patch.ChangedFileCount);
        AssertEx.True(run.Patch.PatchRelativePath is null, "a refused export writes no patch");
        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     The GLOBAL configuration file is reachable too — the sandbox forwards <c>HOME</c>, and
    ///     <c>run_command</c> is not confined to the workspace, so the model can write <c>$HOME/.gitconfig</c>. Both
    ///     the home-directory and the XDG form were verified executing without the environment guard, which is why the
    ///     repository-config rewrite alone is not the whole fix.
    /// </summary>
    /// <remarks>
    ///     <c>HOME</c> is moved on the TEST HOST for the duration, because that is the variable the sandbox provider
    ///     forwards and therefore the only way to exercise the real path — writing the developer's own
    ///     <c>~/.gitconfig</c> is not an option. It is process-global, hence <c>[NotInParallel]</c>, and it is restored
    ///     in a <c>finally</c>.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task Export_WhenAGlobalGitConfigDefinesADriver_NeverRunsIt()
    {
        SkipUnlessRealGitAndProcessJail();

        var marker = MarkerRelativePath("global");
        var payload = PayloadCommand(marker);

        var home = CreateTempDirectory("xe-ah-home");
        await File.WriteAllTextAsync(Path.Combine(home, ".gitconfig"), $"[diff \"pwn\"]\n\ttextconv = {payload}\n");

        var previousHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);

            using var fixture = CreateFixture();
            var run = await fixture.RunAsync(
                ("write_file", new() { ["path"] = $"{WorkspaceAlias}/.gitattributes", ["content"] = "* diff=pwn\n" }),
                ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }));

            AssertEx.False(await MarkerExistsAsync(fixture.Provider, marker), "a driver defined in the GLOBAL git config must not execute during export");
            AssertEx.True(run.Patch.ChangedFileCount > 0, "the guard must not break the diff");
            await AssertHostFolderUnchangedAsync(fixture);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
        }
    }

    /// <summary>
    ///     The audit half of the finding: export's git runs in the same sandbox, over the same workspace, as the
    ///     model's own commands. A <c>commands.jsonl</c> that showed only the model's half would hide exactly the
    ///     invocations this class exists to protect.
    ///     <para>
    ///         Graded on the SERIALIZED file rather than on the records the logger was handed. Those two diverged
    ///         once already — the envelope simply never listed the actor, so every field an operator reads was right
    ///         and the attribution was absent on both halves, with the in-memory assertion still green.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Export_WritesItsOwnGitCommandsToTheRunLog_AttributedToTheNode()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("run_command", new() { ["executable"] = "/bin/echo", ["arguments"] = new[] { "hello" } }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }));

        var logged = (await File.ReadAllLinesAsync(Path.Combine(run.LogPath, "commands.jsonl")))
                     .Where(line => !string.IsNullOrWhiteSpace(line))
                     .Select(line => JsonDocument.Parse(line).RootElement)
                     .ToList();

        // The order is the run's own: the model's turn first, then the export's two diffs after it ended. (The
        // workspace-copy baseline's git runs during PREPARE, before the run log is opened, so it is not in this file.)
        var expectedActors = string.Join(separator: ',', AgentHomeCommandActors.Model, AgentHomeCommandActors.Node, AgentHomeCommandActors.Node);
        var actors = string.Join(separator: ',', logged.Select(record => record.GetProperty("actor").GetString()));
        AssertEx.Equal(expectedActors, actors,
            "commands.jsonl records the model's own command and then both of the export's, each attributed to who ran it");

        var nodeCommands = logged.Where(record => string.Equals(record.GetProperty("actor").GetString(), AgentHomeCommandActors.Node, StringComparison.Ordinal)).ToList();
        AssertEx.True(nodeCommands.TrueForAll(record => string.Equals(record.GetProperty("executable").GetString(), "git", StringComparison.Ordinal)),
            "the node's logged commands are the export's git invocations");
        AssertEx.True(nodeCommands.TrueForAll(record => record.GetProperty("arguments").EnumerateArray().Any(argument => string.Equals(argument.GetString(), "--no-textconv", StringComparison.Ordinal))),
            "the logged argument vector is the one that really ran, belt-and-braces flags included");
    }

    // ---------------------------------------------------------------- harness

    /// <summary>
    ///     The whole loop needs REAL git in the jail and a POSIX shell for the payloads. Skipping VISIBLY is the
    ///     contract; a silent return would report a green that proved nothing about a security control.
    /// </summary>
    private static void SkipUnlessRealGitAndProcessJail()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("BLOCKED: the patch-export hardening negative controls drive the process jail with POSIX utilities and need Linux.");
        }

        if (!IsGitAvailable())
        {
            Skip.Test("BLOCKED: real `git` is required on PATH — these are negative controls for what git executes.");
        }
    }

    private static bool IsGitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return false;
            }

            _ = process.WaitForExit(milliseconds: 10000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     The marker a payload would leave, named as a WORKSPACE-relative path.
    ///     <para>
    ///         It used to be a host temp path. That stopped being a valid probe the moment AgentHome started asking
    ///         for a filesystem boundary: under isolation the sandbox has its own <c>/tmp</c> and cannot see the
    ///         host's, so a payload that "failed" to create a host marker would have proved the mount namespace and
    ///         nothing about the git guard. Inside the workspace the marker is reachable under BOTH modes, so the
    ///         assertion grades the guard either way.
    ///     </para>
    /// </summary>
    private static string MarkerRelativePath(string caseName)
    {
        return $"pwned-{caseName}.marker";
    }

    /// <summary>
    ///     The payload a git config key would name. <c>touch</c> plus the marker path: git appends the blob path it is
    ///     converting, so the command becomes <c>touch &lt;marker&gt; &lt;blob&gt;</c> and the marker appears if — and
    ///     only if — git ran it. A real binary from the sandbox's own read-only <c>/usr</c>, so it resolves under
    ///     isolation too, and it exits at once: a payload that blocked would hang the export rather than prove it safe.
    /// </summary>
    private static string PayloadCommand(string markerRelativePath)
    {
        // BOTH views of the workspace, because the payload runs wherever the node's git runs and that differs by
        // isolation mode: under SandboxIsolationMode.Filesystem the child sees the jail at SandboxIsolatedPaths.Work,
        // so the sandbox-absolute /agent-home/… path does not exist for it; without isolation the opposite is true.
        // `touch` creates what it can and reports the rest, so naming both makes the probe fire under either mode.
        //
        // This is not defensive padding: the first version named only the non-isolated path, and the deliberate-break
        // proof caught it — with the guard REMOVED the tests still passed, because the payload could never have
        // created its marker. A negative control that cannot fire proves nothing.
        return $"/usr/bin/touch {SandboxIsolatedPaths.Work}{AgentHomeGit.WorkspaceSelectedRoot}/{markerRelativePath} "
               + $"{AgentHomeGit.WorkspaceSelectedRoot}/{markerRelativePath}";
    }

    /// <summary>
    ///     Whether the payload's marker exists in the workspace, asked of the PROVIDER rather than of the host
    ///     filesystem — under isolation the workspace is inside a mount namespace, and the provider's own survey is
    ///     the one way to look that works under both modes.
    /// </summary>
    private static async Task<bool> MarkerExistsAsync(ProcessSandboxRuntimeProvider provider, string markerRelativePath)
    {
        var handle = await provider.ConnectAsync(AttachKey());
        var entries = await provider.ListFilesAsync(handle,
            new SandboxListFilesRequest
            {
                DirectoryPath = AgentHomeGit.WorkspaceSelectedRoot,
                MaxEntries = 500
            });

        return entries.Any(entry => entry.EndsWith(markerRelativePath, StringComparison.Ordinal));
    }

    private string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempPaths.Add(directory);
        return directory;
    }

    private static async Task AssertHostFolderUnchangedAsync(ExportFixture fixture)
    {
        // The run works on a COPY. Whatever the payload attempted, the operator's own folder is the thing that must
        // come out byte-identical.
        AssertEx.Equal("# project\nsmal\n", await File.ReadAllTextAsync(Path.Combine(fixture.HostFolder, "README.md")));
        AssertEx.Equal("notes\n", await File.ReadAllTextAsync(Path.Combine(fixture.HostFolder, "notes.txt")));
    }

    private static SandboxAttachKey AttachKey()
    {
        return new SandboxAttachKey
        {
            OwnerUserId = "owner-hardening",
            NodeId = "node-hardening",
            ProviderName = ProcessSandboxRuntimeProvider.Name,
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
    }

    private ExportFixture CreateFixture()
    {
        var clock = TimeProvider.System;
        var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), clock);

        var hostFolder = CreateTempDirectory("xe-ah-src");
        File.WriteAllText(Path.Combine(hostFolder, "README.md"), "# project\nsmal\n");
        File.WriteAllText(Path.Combine(hostFolder, "notes.txt"), "notes\n");

        var resolver = new StaticSelectedFolderResolver(WorkspaceAlias, hostFolder);
        var root = CreateTempDirectory("xe-ah-state");
        var options = Options.Create(new AgentHomeOptions
        {
            RootPath = root,
            CommandTimeoutSeconds = 120,
            MaxRunSeconds = 600
        });
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithAgentHomeCommandTimeoutSeconds(120)
                                                     .WithAgentHomePrepareTimeoutSeconds(300)
                                                     .Build();

        var manifestService = new AgentHomeManifestService(new FakeNodeDataDirectory(root), options, provider, clock, NullLogger<AgentHomeManifestService>.Instance);
        var serviceProvider = new ServiceCollection()
                              .AddScoped<ISelectedFolderResolver>(_ => resolver)
                              // The REAL file-writing logger: the audit test below reads the run's commands.jsonl back
                              // off disk, which is the artefact an operator audits and the one that lost the actor.
                              .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(clock))
                              .BuildServiceProvider();

        var leases = new AgentHomeExecutionLeaseManager();
        var isolation = new AgentHomeWorkspaceIsolation(provider, leases, NullLogger<AgentHomeWorkspaceIsolation>.Instance);
        var workspaceService = new AgentHomeWorkspaceService(provider,
            isolation,
            new SensitiveFileExclusionService(),
            runtimeSettings,
            NullLogger<AgentHomeWorkspaceService>.Instance);
        var patchService = new AgentHomePatchService(provider, runtimeSettings, clock, NullLogger<AgentHomePatchService>.Instance);

        var reader = new CoderWorkspaceReader(provider,
            new StaticIdentityProvider(),
            leases,
            new SensitiveFileExclusionService(),
            Options.Create(new CoderOptions()),
            Options.Create(new AgentHomeOptions()));

        var chatClient = new ScriptedGitPayloadChatClient();
        var executor = new AgentHomeGoalExecutor(chatClient,
            provider,
            reader,
            new FakeModelTrustResolver(),
            options,
            clock,
            NullLoggerFactory.Instance,
            NullLogger<AgentHomeGoalExecutor>.Instance);

        var service = new AgentHomeService(manifestService,
            provider,
            new StaticIdentityProvider(),
            leases,
            isolation,
            workspaceService,
            patchService,
            executor,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            Options.Create(new SandboxOptions()),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            runtimeSettings,
            new FakeConversationUploadedFileStore(),
            clock,
            NullLogger<AgentHomeService>.Instance);

        return new ExportFixture(service, provider, manifestService, serviceProvider, chatClient, hostFolder, resolver.FolderId);
    }

    private sealed class ExportFixture : IDisposable
    {
        private readonly AgentHomeManifestService _manifestService;
        private readonly ScriptedGitPayloadChatClient _chatClient;
        private readonly ProcessSandboxRuntimeProvider _provider;
        private readonly AgentHomeService _service;
        private readonly ServiceProvider _serviceProvider;
        private readonly Guid _folderId;

        public ExportFixture(AgentHomeService service,
            ProcessSandboxRuntimeProvider provider,
            AgentHomeManifestService manifestService,
            ServiceProvider serviceProvider,
            ScriptedGitPayloadChatClient chatClient,
            string hostFolder,
            Guid folderId)
        {
            _service = service;
            _provider = provider;
            _manifestService = manifestService;
            _serviceProvider = serviceProvider;
            _chatClient = chatClient;
            HostFolder = hostFolder;
            _folderId = folderId;
        }

        /// <summary>The real provider, so a test can ask it what is in the workspace under either isolation mode.</summary>
        public ProcessSandboxRuntimeProvider Provider => _provider;

        public string HostFolder { get; }

        /// <summary>Runs the whole lifecycle — copy, baseline, the scripted inner loop, export — on the real jail.</summary>
        public async Task<AgentHomeRunResult> RunAsync(params (string Tool, Dictionary<string, object?> Arguments)[] script)
        {
            _chatClient.Script = script;

            // The ambient root a real chat turn seeds; without it the executor refuses to run at all.
            using var root = SpawnContext.BeginRoot(fanOutCap: 1, cloudSpawnCap: 0, Model);
            return await _service.RunLifecycleAsync(new AgentHomeRunLifecycleRequest
            {
                SelectedFolderIds = [_folderId.ToString()],
                Goal = "fix the typo in the readme",
                AllowedActions =
                [
                    AgentHomeAllowedActions.ReadWorkspace,
                    AgentHomeAllowedActions.WriteWorkspace,
                    AgentHomeAllowedActions.RunCommands,
                    AgentHomeAllowedActions.ExportPatch
                ]
            });
        }

        public void Dispose()
        {
            _provider.Dispose();
            _manifestService.Dispose();
            _serviceProvider.Dispose();
            _chatClient.Dispose();
        }
    }

    /// <summary>Plays the planted-payload script against whatever tools the executor offered, then answers with text.</summary>
    private sealed class ScriptedGitPayloadChatClient : IChatClient
    {
        private int _calls;

        public (string Tool, Dictionary<string, object?> Arguments)[] Script { get; set; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
            }

            var functions = options?.Tools?.OfType<AIFunction>().ToArray() ?? [];
            foreach (var (tool, arguments) in Script)
            {
                var function = AssertEx.NotNull(Array.Find(functions, candidate => string.Equals(candidate.Name, tool, StringComparison.Ordinal)),
                    $"the scripted tool '{tool}' must be offered for this run");

                var callArguments = new AIFunctionArguments(StringComparer.Ordinal);
                foreach (var (key, value) in arguments)
                {
                    callArguments[key] = value;
                }

                _ = await function.InvokeAsync(callArguments, cancellationToken);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StaticIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity { OwnerUserId = "owner-hardening", NodeId = "node-hardening" });
        }
    }

    private sealed class StaticSelectedFolderResolver : ISelectedFolderResolver
    {
        private readonly ResolvedSelectedFolder _folder;

        public StaticSelectedFolderResolver(string alias, string hostPath)
        {
            FolderId = Guid.NewGuid();
            _folder = new ResolvedSelectedFolder { Id = FolderId, Alias = alias, HostPath = hostPath, Mode = SelectedFolderMode.Copy };
        }

        public Guid FolderId { get; }

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SelectedFolderReference> references = [new() { Id = _folder.Id.ToString(), Alias = _folder.Alias }];
            return Task.FromResult(references);
        }

        public Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.Equals(id, _folder.Id.ToString(), StringComparison.Ordinal)
                || string.Equals(id, _folder.Alias, StringComparison.Ordinal))
            {
                return Task.FromResult(_folder);
            }

            throw new SelectedFolderValidationException($"Unknown selected folder id '{id}'.");
        }
    }
}
