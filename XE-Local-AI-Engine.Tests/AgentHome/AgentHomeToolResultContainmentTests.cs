namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Client.Services.Coder.Implementation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;
using CategoryAttribute = CategoryAttribute;

/// <summary>
///     The one rule the <c>run_in_agent_home</c> result may never break: no command output and no workspace file
///     bytes cross back into the OUTER model's context.
/// </summary>
/// <remarks>
///     The outer model holds the node's other tools, so a workspace-authored line arriving as the result of the
///     tool it just called is steering text with a delivery mechanism; the summary is built from node-derived facts
///     only — counts, exit codes, byte sizes, durations and a closed set of status tokens. Graded on the REAL
///     process jail, because a stub cannot plant the marker in a real command's stdout and a real file's contents.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class AgentHomeToolResultContainmentTests : IDisposable
{
    private const string Model = "qwen3:8b";
    private const string WorkspaceAlias = "selected-project";

    /// <summary>Distinctive enough that a substring search for it cannot match anything the node itself wrote.</summary>
    private const string Marker = "XEMARKERaf19c3LEAK";

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

    [Test]
    public async Task ToolResult_CarriesNeitherCommandOutputNorWorkspaceBytes_ButStillReportsTheWork()
    {
        SkipUnlessRealGitAndIsolatedProcessJail();

        using var fixture = CreateFixture();

        // The marker enters by all three routes a summary could widen into: the CONTENT of a file the model wrote
        // (the tracked README, so it also reaches the patch), the PATH it chose, and the STDOUT of a command.
        var result = await fixture.ExecuteToolAsync(("write_file", new()
            {
                ["path"] = $"{WorkspaceAlias}/README.md",
                ["content"] = $"# project\n{Marker}\n"
            }),
            ("write_file", new()
            {
                ["path"] = $"{WorkspaceAlias}/{Marker}.md",
                ["content"] = "notes\n"
            }),
            ("run_command", new()
            {
                ["executable"] = "/bin/echo",
                ["arguments"] = new[]
                {
                    Marker
                }
            }));

        AssertEx.False(result.Contains(Marker, StringComparison.Ordinal),
            $"no command stdout, file content or model-chosen path may reach the outer model. The result was: {result}");

        // The machine-readable header is line one and node-built, on the real jail as in the unit tests: whoever
        // decides which run's patch to offer reads it start-anchored, ahead of any model-authored byte.
        var firstLine = result[..result.IndexOf('\n', StringComparison.Ordinal)];
        AssertEx.True(firstLine.StartsWith("[agent-home run=run-", StringComparison.Ordinal)
                      && firstLine.EndsWith(" outcome=Completed patch=exported]", StringComparison.Ordinal),
            $"line one is the node's header for this run. It was: {firstLine}");

        // …and the containment must not have been bought by returning nothing: the node's own account of the run is
        // still there, which is the whole point of the summary the outer model reads.
        AssertEx.Contains(result, "2 file(s) written", StringComparison.Ordinal, $"the node still says how much was written — a count, never the bytes. Result: {result}");
        AssertEx.Contains(result, "commands: 1 run (0 non-zero exit, 0 did not complete)", StringComparison.Ordinal,
            $"the node still says what ran and how it exited — never what it printed. Result: {result}");
        AssertEx.Contains(result, "file(s) changed", StringComparison.Ordinal, $"the patch is still reported. Result: {result}");
        AssertEx.Contains(result, "This run has ended", StringComparison.Ordinal, $"the fixed closing sentence is there. Result: {result}");
    }

    /// <summary>
    ///     The written-file gap is reported to the model as COUNTS. The paths behind those counts are model-chosen,
    ///     so they may reach the run's own log and nothing else.
    /// </summary>
    [Test]
    public async Task ToolResult_ReportsTheWrittenFileGapAsCountsOnly_WhileTheRunLogKeepsThePaths()
    {
        SkipUnlessRealGitAndIsolatedProcessJail();

        using var fixture = CreateFixture();

        // The marker is the NAME of a file the run's own .gitignore then hides, so the path lands in the gap and
        // the only question left is where its name is allowed to appear.
        var result = await fixture.ExecuteToolAsync(("write_file", new()
            {
                ["path"] = $"{WorkspaceAlias}/.gitignore",
                ["content"] = $"{Marker}.txt\n"
            }),
            ("write_file", new()
            {
                ["path"] = $"{WorkspaceAlias}/{Marker}.txt",
                ["content"] = "hidden\n"
            }),
            ("write_file", new()
            {
                ["path"] = $"{WorkspaceAlias}/README.md",
                ["content"] = "# project\nedited\n"
            }));

        AssertEx.False(result.Contains(Marker, StringComparison.Ordinal),
            $"the gap note names no path, not even one the patch left out. The result was: {result}");
        AssertEx.Contains(result,
            "NOTE: 1 file(s) the run wrote are not part of this patch (1 ignored, 0 deleted, 0 unchanged, 0 unexplained).",
            StringComparison.Ordinal,
            $"the model is told how many writes the patch does not carry, and why. Result: {result}");

        var runId = RunIdFromHeader(result);
        var eventsPath = Directory.EnumerateFiles(fixture.StateRoot, "events.jsonl", SearchOption.AllDirectories)
                                  .Single(path => path.Contains(runId, StringComparison.Ordinal));
        AssertEx.Contains(await File.ReadAllTextAsync(eventsPath), Marker, StringComparison.Ordinal,
            "the operator's own record of the run keeps the paths the model may not see");
    }

    // ---------------------------------------------------------------- harness

    private static string RunIdFromHeader(string result)
    {
        var start = result.IndexOf("run=", StringComparison.Ordinal) + "run=".Length;
        return result[start..result.IndexOf(' ', start)];
    }

    private static void SkipUnlessRealGitAndIsolatedProcessJail()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("BLOCKED: the containment probe drives the process jail with POSIX utilities and needs Linux.");
        }

        if (!IsGitAvailable())
        {
            Skip.Test("BLOCKED: real `git` is required on PATH — the run's baseline and patch export are real git here.");
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

    private string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempPaths.Add(directory);
        return directory;
    }

    private ContainmentFixture CreateFixture()
    {
        var clock = TimeProvider.System;
        var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), clock);
        if (!provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            provider.Dispose();
            Skip.Test("BLOCKED: this host cannot deliver SandboxIsolationMode.Filesystem, so run_command — the tool that produces the command output this probe plants — is withheld.");
        }

        var hostFolder = CreateTempDirectory("xe-ah-contain-src");
        File.WriteAllText(Path.Combine(hostFolder, "README.md"), "# project\n");

        var resolver = new StaticSelectedFolderResolver(WorkspaceAlias, hostFolder);
        var root = CreateTempDirectory("xe-ah-contain-state");
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
                              .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(clock))
                              .BuildServiceProvider();

        var leases = new AgentHomeExecutionLeaseManager();
        var isolation = new AgentHomeWorkspaceIsolation(provider, leases, NullLogger<AgentHomeWorkspaceIsolation>.Instance);
        var workspaceService = new AgentHomeWorkspaceService(provider,
            isolation,
            new SensitiveFileExclusionService(),
            runtimeSettings,
            clock,
            NullLogger<AgentHomeWorkspaceService>.Instance);
        var patchService = new AgentHomePatchService(provider, runtimeSettings, clock, NullLogger<AgentHomePatchService>.Instance);

        var reader = new CoderWorkspaceReader(provider,
            new StaticIdentityProvider(),
            leases,
            new SensitiveFileExclusionService(),
            Options.Create(new CoderOptions()),
            Options.Create(new AgentHomeOptions()));

        var chatClient = new ScriptedInnerChatClient();
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
            new AgentHomeRunExecutionRegistry(),
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

        var gateway = new AgentHomeToolGateway(service, runtimeSettings);
        return new ContainmentFixture(gateway, provider, manifestService, serviceProvider, chatClient, root, resolver.FolderId);
    }

    private sealed class ContainmentFixture : IDisposable
    {
        private readonly ScriptedInnerChatClient _chatClient;
        private readonly Guid _folderId;
        private readonly AgentHomeToolGateway _gateway;
        private readonly AgentHomeManifestService _manifestService;
        private readonly ProcessSandboxRuntimeProvider _provider;
        private readonly ServiceProvider _serviceProvider;

        public ContainmentFixture(AgentHomeToolGateway gateway,
            ProcessSandboxRuntimeProvider provider,
            AgentHomeManifestService manifestService,
            ServiceProvider serviceProvider,
            ScriptedInnerChatClient chatClient,
            string stateRoot,
            Guid folderId)
        {
            _gateway = gateway;
            _provider = provider;
            _manifestService = manifestService;
            _serviceProvider = serviceProvider;
            _chatClient = chatClient;
            StateRoot = stateRoot;
            _folderId = folderId;
        }

        /// <summary>The AgentHome state root, under which this run's own logs live.</summary>
        public string StateRoot { get; }

        /// <summary>Runs the whole lifecycle through the TOOL GATEWAY, so the assertion grades what the model is handed.</summary>
        public async Task<string> ExecuteToolAsync(params (string Tool, Dictionary<string, object?> Arguments)[] script)
        {
            _chatClient.Script = script;

            // The ambient root a real chat turn seeds; without it the executor refuses to run at all.
            using var root = SpawnContext.BeginRoot(fanOutCap: 1, cloudSpawnCap: 0, Model);
            return await _gateway.ExecuteAsync(new AgentHomeRunToolRequest
            {
                Goal = "record the marker in the workspace",
                SelectedFolderIds = [_folderId.ToString()],
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

    /// <summary>Plays the script against whatever tools the executor offered, then answers with text.</summary>
    private sealed class ScriptedInnerChatClient : IChatClient
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
            return Task.FromResult(new AgentHomeOwnerIdentity
            {
                OwnerUserId = "owner-containment",
                NodeId = "node-containment"
            });
        }
    }

    private sealed class StaticSelectedFolderResolver : ISelectedFolderResolver
    {
        private readonly ResolvedSelectedFolder _folder;

        public StaticSelectedFolderResolver(string alias, string hostPath)
        {
            FolderId = Guid.NewGuid();
            _folder = new ResolvedSelectedFolder
            {
                Id = FolderId,
                Alias = alias,
                HostPath = hostPath,
                Mode = SelectedFolderMode.Copy
            };
        }

        public Guid FolderId { get; }

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SelectedFolderReference> references =
            [
                new()
                {
                    Id = _folder.Id.ToString(),
                    Alias = _folder.Alias
                }
            ];
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
