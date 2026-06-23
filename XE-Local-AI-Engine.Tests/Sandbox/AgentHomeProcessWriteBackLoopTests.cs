namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The acceptance loop on the REAL <see cref="ProcessSandboxRuntimeProvider" />: copy a selected folder into the
///     jail → run the per-profile probe + create a real-git baseline → export a patch of a change made in the jail →
///     apply that patch onto a host folder. This proves the copy → run → export → apply loop is unchanged on the
///     process provider, end to end, with real <c>git</c> and a real spawned command — no Docker, no fake. It mirrors
///     the <see cref="XE_Local_AI_Engine.Tests.AgentHome.AgentHomeServiceTests" /> harness shape but swaps in the
///     process provider. It self-skips if <c>git</c> is not on PATH (the loop needs real git in the jail).
/// </summary>
public sealed class AgentHomeProcessWriteBackLoopTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 6, day: 17, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    public async Task AgentHome_WriteBackLoop_OnProcessProvider()
    {
        if (!IsGitAvailable())
        {
            Skip.Test("BLOCKED: real `git` is required on PATH for the process-provider write-back loop.");
        }

        var clock = new FixedClock(FixedNow);
        using var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), clock);

        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        // The SAME pre-image seeds both the selected source folder (copied into the jail) and stays on the host so the
        // exported patch applies cleanly back onto the host folder (apply step).
        var hostFolder = CreateSourceFolder(("README.md", "# project\nalpha\n"));
        resolver.Add(folderId, "selected-project", hostFolder);

        using var harness = CreateHarness(clock, provider, resolver);

        // 1) COPY — prepare copies the selected folder into the jail and creates the real-git baseline.
        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });
        AssertEx.Equal(AgentHomeStatus.Ready, prepared.Layout.Manifest.Status);
        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, prepared.Handle.ProviderName);
        AssertEx.Equal(expected: 1, prepared.FolderSnapshots.Count);
        AssertEx.Equal(SelectedFolderCopyStatus.Copied, prepared.FolderSnapshots[0].Status);

        // 2) RUN — change the copied file in the jail (an "agent edit") then run the probe; the command runs with the
        // copied workspace as CWD so the later diff sees the change.
        var jailRelativeReadme = AgentHomeGit.WorkspaceSelectedRoot + "/selected-project/README.md";
        await provider.CopyIntoAsync(prepared.Handle, new SandboxCopyRequest
        {
            SourcePath = WriteHostTempFile("# project\nalpha\nbravo\n"),
            DestinationPath = jailRelativeReadme
        });

        // 3) EXPORT — run + patch export (export_patch granted). The diff is taken against the baseline, so the agent
        // edit surfaces as a changes.patch written host-side under the run dir.
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "modify the readme",
            AllowedActions = ["read_workspace", "run_commands", "export_patch"]
        });

        AssertEx.True(run.Completed, $"the real probe completes on the process provider (exit {run.ExitCode})");
        AssertEx.Equal(expected: 1, run.Patch.ChangedFileCount);
        AssertEx.False(run.Patch.Blocked, "the one-line change is under budget");
        var patchFile = Path.Combine(prepared.Layout.RootPath, "runs", run.RunId, "patches", "changes.patch");
        AssertEx.True(File.Exists(patchFile), "the exported changes.patch must be written host-side");
        var patchText = await File.ReadAllTextAsync(patchFile);
        AssertEx.Contains(patchText, "bravo");

        // 4) APPLY — land the exported patch onto the host folder via the real apply service.
        var applyService = CreatePatchApplyService(harness.Options, resolver);
        var applied = await applyService.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = run.RunId
        });

        AssertEx.True(applied.Applied, $"the exported patch applies to the host. rejections: {string.Join(separator: ';', applied.Rejections)}");
        var hostReadme = await File.ReadAllTextAsync(Path.Combine(hostFolder, "README.md"));
        AssertEx.Contains(hostReadme, "bravo");
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

            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static NodePatchApplyService CreatePatchApplyService(IOptions<AgentHomeOptions> options, ISelectedFolderResolver resolver)
    {
        var serviceProvider = new ServiceCollection()
                              .AddScoped(_ => resolver)
                              .BuildServiceProvider();
        // RootPath is set on the options, so the data-dir root is unused by ResolveAgentHomeRoot — the apply service and
        // the run service both resolve <RootPath>/agent-home, so the exported changes.patch is found.
        return new NodePatchApplyService(resolver,
            options,
            new FakeNodeDataDirectory(options.Value.RootPath ?? string.Empty),
            new StaticIdentityProvider("owner-a", "node-1"),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodePatchApplyService>.Instance);
    }

    private string CreateSourceFolder(params (string RelativePath, string Content)[] files)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agenthome-proc-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var (relativePath, content) in files)
        {
            var full = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        _tempRoots.Add(directory);
        return directory;
    }

    private string WriteHostTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "agenthome-proc-edit-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        _tempRoots.Add(path);
        return path;
    }

    private ServiceHarness CreateHarness(TimeProvider clock,
        ISandboxRuntimeProvider provider,
        ISelectedFolderResolver resolver)
    {
        var root = Path.Combine(Path.GetTempPath(), "agenthome-proc-svc-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);

        var options = Options.Create(new AgentHomeOptions
        {
            RootPath = root,
            CommandTimeoutSeconds = 120
        });
        var manifestService = new AgentHomeManifestService(new FakeNodeDataDirectory(root), options, provider, clock, NullLogger<AgentHomeManifestService>.Instance);

        var serviceProvider = new ServiceCollection()
                              .AddScoped(_ => resolver)
                              .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(clock))
                              .BuildServiceProvider();

        var memoryProposalService = new AgentHomeMemoryProposalService(NullLogger<AgentHomeMemoryProposalService>.Instance);
        var workspaceService = new AgentHomeWorkspaceService(provider,
            new SensitiveFileExclusionService(),
            options,
            NullLogger<AgentHomeWorkspaceService>.Instance);
        var patchService = new AgentHomePatchService(provider,
            options,
            NullLogger<AgentHomePatchService>.Instance);

        var service = new AgentHomeService(manifestService,
            provider,
            new StaticIdentityProvider("owner-a", "node-1"),
            workspaceService,
            patchService,
            memoryProposalService,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            clock,
            NullLogger<AgentHomeService>.Instance);

        return new ServiceHarness(service, manifestService, serviceProvider, options);
    }

    private sealed class ServiceHarness : IDisposable
    {
        private readonly AgentHomeManifestService _manifestService;
        private readonly ServiceProvider _serviceProvider;

        public ServiceHarness(AgentHomeService service,
            AgentHomeManifestService manifestService,
            ServiceProvider serviceProvider,
            IOptions<AgentHomeOptions> options)
        {
            Service = service;
            _manifestService = manifestService;
            _serviceProvider = serviceProvider;
            Options = options;
        }

        public AgentHomeService Service { get; }

        public IOptions<AgentHomeOptions> Options { get; }

        public void Dispose()
        {
            _manifestService.Dispose();
            _serviceProvider.Dispose();
        }
    }

    private sealed class StaticIdentityProvider : IAgentHomeIdentityProvider
    {
        public StaticIdentityProvider(string ownerUserId, string nodeId)
        {
            OwnerUserId = ownerUserId;
            NodeId = nodeId;
        }

        public string OwnerUserId { get; }

        public string NodeId { get; }

        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity(OwnerUserId, NodeId));
        }
    }

    private sealed class FakeSelectedFolderResolver : ISelectedFolderResolver
    {
        private readonly Dictionary<Guid, ResolvedSelectedFolder> _folders = [];

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SelectedFolderReference> references =
                _folders.Values.Select(folder => new SelectedFolderReference(folder.Id.ToString(), folder.Alias)).ToList();
            return Task.FromResult(references);
        }

        public Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default)
        {
            if (Guid.TryParse(id, out var parsed) && _folders.TryGetValue(parsed, out var folder))
            {
                return Task.FromResult(folder);
            }

            throw new SelectedFolderValidationException($"Unknown selected folder id '{id}'.");
        }

        public void Add(Guid id, string alias, string hostPath)
        {
            _folders[id] = new ResolvedSelectedFolder(id, alias, hostPath, SelectedFolderMode.Copy);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedClock(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
