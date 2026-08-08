namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The provider-neutral half of the mount broker: what a handle reports, and what the process provider does with a
///     mount request.
///     <para>
///         The process provider's contribution is the one that is easiest to get wrong by being helpful. It has no
///         mount layer, so the only honest answer is an identity map — and the temptation is to "improve" it into a
///         jail over the runtime directories, which would silently change the preserved-workspace contract for callers
///         that never asked for it. These assert the identity, not merely that a mount list came back.
///     </para>
/// </summary>
public sealed class SandboxMountBrokerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-mount-broker-" + Guid.NewGuid().ToString("N"));

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
    public void TryResolveSandboxPath_TranslatesAMountRootAndAnythingBeneathIt()
    {
        var handle = Handle([new SandboxMountBinding("/host/runtime/home", "/xe-runtime/home", ReadOnly: false)]);

        AssertEx.Equal("/xe-runtime/home", handle.TryResolveSandboxPath("/host/runtime/home"));
        AssertEx.Equal("/xe-runtime/home/.nuget/packages", handle.TryResolveSandboxPath("/host/runtime/home/.nuget/packages"));
    }

    [Test]
    public void TryResolveSandboxPath_PrefersTheLongestMatchingMount()
    {
        // A nested mount must win over the one it sits inside — the read-only .git/config layered over the workspace is
        // exactly this shape, and answering with the workspace's mapping would name the wrong file.
        var handle = Handle([
            new SandboxMountBinding("/host/workspace", "/workspace", ReadOnly: false),
            new SandboxMountBinding("/host/workspace/.git/config", "/workspace/.git/config", ReadOnly: true)
        ]);

        AssertEx.Equal("/workspace/.git/config", handle.TryResolveSandboxPath("/host/workspace/.git/config"));
        AssertEx.Equal("/workspace/src/Lib.cs", handle.TryResolveSandboxPath("/host/workspace/src/Lib.cs"));
    }

    [Test]
    public void TryResolveSandboxPath_WhenNoMountCoversThePath_ReturnsNullRatherThanTheHostPath()
    {
        // Returning the host path would compose a command that fails deep inside a build against a directory the
        // sandbox has never heard of. Null is what lets the caller refuse at composition time instead.
        var handle = Handle([new SandboxMountBinding("/host/workspace", "/workspace", ReadOnly: false)]);

        AssertEx.Null(handle.TryResolveSandboxPath("/host/runtime/home"));
    }

    [Test]
    public async Task ProcessProvider_MapsEveryRequestedMountToItsOwnHostPath()
    {
        // A host child sees the host filesystem, so the requested sandbox path is DISCARDED and the host path is what
        // is reported. Asserting the requested path is absent is the point: honouring it would name a directory this
        // provider never created.
        var workspace = CreateDirectory("workspace");
        var home = CreateDirectory("runtime/home");
        using var provider = CreateProvider();

        var handle = await provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = home,
                SandboxPath = "/xe-runtime/home",
                ReadOnly = false
            }
        ]));

        AssertEx.Equal(home, handle.TryResolveSandboxPath(home));
        AssertEx.Contains(handle.Mounts, mount => string.Equals(mount.HostPath, workspace, StringComparison.Ordinal)
                                                  && string.Equals(mount.SandboxPath, workspace, StringComparison.Ordinal));
        AssertEx.Empty(handle.Mounts.Where(static mount => mount.SandboxPath.StartsWith("/xe-runtime", StringComparison.Ordinal)));
    }

    [Test]
    public async Task ProcessProvider_RefusesAReadOnlyMountRatherThanServingItWritable()
    {
        var workspace = CreateDirectory("ro-workspace");
        var config = CreateFile("ro-workspace/.git/config", "[core]\n");
        using var provider = CreateProvider();

        // The provider does not advertise SupportsReadOnlyMounts, and a capability it does not advertise must be
        // refused rather than silently downgraded — a caller would otherwise believe a file is protected that anything
        // in the sandbox can overwrite.
        AssertEx.False(provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsReadOnlyMounts));
        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = config,
                SandboxPath = "/.git/config",
                ReadOnly = true
            }
        ])));

        AssertEx.Contains(exception.Message, "read-only");
    }

    [Test]
    public async Task ProcessProvider_RefusesAMountSourceThatDoesNotExist()
    {
        var workspace = CreateDirectory("missing-source-workspace");
        using var provider = CreateProvider();

        await AssertEx.ThrowsAsync<DirectoryNotFoundException>(() => provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = Path.Combine(_root, "never-created"),
                SandboxPath = "/xe-runtime/home",
                ReadOnly = false
            }
        ])));
    }

    [Test]
    public async Task ProcessProvider_WithNoMountsRequested_StillReportsTheWorkspaceAndNothingElse()
    {
        // The regression guard for "do not helpfully start jailing things": an AgentHome-shaped request must produce
        // exactly the mapping it produced before the broker existed.
        var workspace = CreateDirectory("plain-workspace");
        using var provider = CreateProvider();

        var handle = await provider.CreateOrAttachAsync(Request(workspace, mounts: null));

        AssertEx.Equal(expected: 1, handle.Mounts.Count);
        AssertEx.Equal(workspace, handle.Mounts[0].SandboxPath);
    }

    private static SandboxHandle Handle(IReadOnlyList<SandboxMountBinding> mounts)
    {
        return new SandboxHandle
        {
            ProviderName = "test",
            SandboxId = "sandbox-1",
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = "test",
                RuntimeProfile = "development",
                ManifestVersion = 1
            },
            CreatedAt = DateTimeOffset.UnixEpoch,
            ManifestVersion = 1,
            Mounts = mounts
        };
    }

    private static SandboxCreateRequest Request(string workspace, IReadOnlyList<SandboxMount>? mounts)
    {
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = Guid.NewGuid().ToString("N"),
                NodeId = "node-1",
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "development",
                ManifestVersion = 1
            },
            RuntimeProfile = "development",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = workspace
            },
            Mounts = mounts
        };
    }

    private static ProcessSandboxRuntimeProvider CreateProvider()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
    }

    private string CreateDirectory(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateFile(string relative, string content)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
