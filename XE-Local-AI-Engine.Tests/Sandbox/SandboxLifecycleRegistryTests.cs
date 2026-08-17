namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for <see cref="SandboxLifecycleRegistry" /> — which jails exist, on its own, with no child
///     process involved. <see cref="ProcessSandboxRuntimeProviderTests" /> keeps the same attach/evict behavior pinned
///     at the provider level; these cases pin the registry directly, and add the two rules that only it can be asked
///     about: a trusted host workspace binding must match on re-attach, and terminating a jail bound to one must NOT
///     delete the user's directory.
/// </summary>
public sealed class SandboxLifecycleRegistryTests : IDisposable
{
    private readonly string _jailRoot = Path.Combine(Path.GetTempPath(), "xe-registry-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _tempPaths = [];

    public SandboxLifecycleRegistryTests()
    {
        Directory.CreateDirectory(_jailRoot);
        _tempPaths.Add(_jailRoot);
    }

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
        }
    }

    [Test]
    public async Task CreateOrAttach_WithTheSameKey_ReturnsTheSameJail_AndSanitizesTheNodeSegment()
    {
        var registry = CreateRegistry();
        var key = Key(node: "node/1 alpha");

        var handle = await registry.CreateOrAttachAsync(CreateRequest(key));
        var again = await registry.CreateOrAttachAsync(CreateRequest(key));

        // Attach is idempotent by key: the second create is an attach, not a second jail.
        AssertEx.Equal(handle.SandboxId, again.SandboxId);
        AssertEx.Equal(expected: 1, Directory.GetDirectories(_jailRoot).Length);

        // Every non-alphanumeric character of the node id is replaced, so the id is always a safe path segment.
        AssertEx.True(handle.SandboxId.StartsWith("process-node_1_alpha-", StringComparison.Ordinal),
            $"the sandbox id must carry a sanitized node segment, got '{handle.SandboxId}'.");
        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, handle.ProviderName);
    }

    [Test]
    public async Task CreateOrAttach_UnderADifferentOwnerOnTheSameNode_EvictsAndDeletesTheOldJail()
    {
        var registry = CreateRegistry();

        var first = await registry.CreateOrAttachAsync(CreateRequest(Key()));
        var firstJail = registry.GetAliveState(first).JailRoot;
        AssertEx.True(Directory.Exists(firstJail), "the first create must have made its jail directory.");

        var second = await registry.CreateOrAttachAsync(CreateRequest(Key(owner: "owner-b")));

        // An owner change on the same node is never an attach — the old jail is killed, removed and deleted.
        AssertEx.NotEqual(first.SandboxId, second.SandboxId);
        AssertEx.Throws<SandboxHandleInvalidException>(() => registry.GetAliveState(first));
        AssertEx.False(Directory.Exists(firstJail), "the evicted jail directory must be deleted, not left behind.");
        AssertEx.True(Directory.Exists(registry.GetAliveState(second).JailRoot));
    }

    [Test]
    public async Task RemoveAndTerminate_DropsOnlyThatJail_AndTerminateAllClearsTheRest()
    {
        var registry = CreateRegistry();

        var kept = await registry.CreateOrAttachAsync(CreateRequest(Key(node: "node-keep")));
        var killed = await registry.CreateOrAttachAsync(CreateRequest(Key(node: "node-kill")));
        var keptJail = registry.GetAliveState(kept).JailRoot;
        var killedJail = registry.GetAliveState(killed).JailRoot;

        registry.RemoveAndTerminate(killed.SandboxId);

        AssertEx.Throws<SandboxHandleInvalidException>(() => registry.GetAliveState(killed));
        AssertEx.False(Directory.Exists(killedJail));
        AssertEx.Equal(kept.SandboxId, registry.GetAliveState(kept).Handle.SandboxId);

        // Terminating an id that is already gone is a no-op, not a throw — kill is called on best-effort teardown paths.
        registry.RemoveAndTerminate(killed.SandboxId);

        registry.TerminateAll();

        AssertEx.Throws<SandboxHandleInvalidException>(() => registry.GetAliveState(kept));
        AssertEx.False(Directory.Exists(keptJail));
    }

    [Test]
    public async Task CreateOrAttach_BoundToATrustedHostWorkspace_RefusesAMismatchedRebind_AndPreservesTheDirectory()
    {
        var registry = CreateRegistry();
        var workspace = CreateTempDirectory("xe-workspace-");
        var otherWorkspace = CreateTempDirectory("xe-workspace-other-");
        var key = Key();

        var handle = await registry.CreateOrAttachAsync(CreateRequest(key, workspace));

        // The workspace IS the jail: nothing is copied, so the user's checkout is what the sandbox runs in.
        AssertEx.Equal(workspace, registry.GetAliveState(handle).JailRoot);

        // Re-attaching under the same binding is the idempotent path.
        AssertEx.Equal(handle.SandboxId, (await registry.CreateOrAttachAsync(CreateRequest(key, workspace))).SandboxId);

        // A different workspace, or none at all, is a different sandbox contract — refused rather than silently rebound.
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => registry.CreateOrAttachAsync(CreateRequest(key, otherWorkspace)));
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => registry.CreateOrAttachAsync(CreateRequest(key)));

        // Killing a workspace-bound sandbox must never delete the user's directory.
        registry.RemoveAndTerminate(handle.SandboxId);
        AssertEx.True(Directory.Exists(workspace), "a trusted host workspace must survive the sandbox that was bound to it.");
    }

    private SandboxLifecycleRegistry CreateRegistry()
    {
        return new SandboxLifecycleRegistry(_jailRoot,
            new SandboxLauncher(new NoContainmentProbe()),
            TimeProvider.System);
    }

    private string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempPaths.Add(path);
        return path;
    }

    private static SandboxCreateRequest CreateRequest(SandboxAttachKey attachKey, string? trustedHostWorkspace = null)
    {
        return new SandboxCreateRequest
        {
            AttachKey = attachKey,
            RuntimeProfile = "dotnet-agent-home",
            // Unrestricted is the only posture a host with no containment mechanism can honestly serve; anything else
            // is rejected up front by the launch policy and would make these lifecycle cases untestable.
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            TrustedHostWorkspace = trustedHostWorkspace is null
                ? null
                : new SandboxTrustedHostWorkspace
                {
                    RootPath = trustedHostWorkspace
                }
        };
    }

    private static SandboxAttachKey Key(string owner = "owner-1", string node = "node-1")
    {
        return new SandboxAttachKey
        {
            OwnerUserId = owner,
            NodeId = node,
            ProviderName = ProcessSandboxRuntimeProvider.Name,
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = 1
        };
    }

    private sealed class NoContainmentProbe : ISandboxContainmentProbe
    {
        public SandboxContainment Containment => SandboxContainment.None;
    }
}
