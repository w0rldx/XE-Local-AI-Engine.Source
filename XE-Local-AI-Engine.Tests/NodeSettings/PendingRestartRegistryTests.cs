namespace XE_Local_AI_Engine.Tests.NodeSettings;

using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     A restart-gated field is pending exactly while its stored value differs from the one the node started with; a
///     live field never is.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class PendingRestartRegistryTests
{
    private static readonly StoredNodeSettings Boot = new()
    {
        ChatCacheReuse = 128,
        KvCacheType = "f16",
        LlamaMaxLoadedProcesses = 2,
        LlamaIdleTimeToLiveSeconds = 900,
        ImageMaxLoadedProcesses = 1,
        ImageIdleTimeToLiveSeconds = 900
    };

    // The real stored-else-seed resolution, unseeded: an unset switch reads its options default.
    private static readonly Func<StoredNodeSettings, NodeSettingsEffectiveValues> Resolve = SeededNodeRuntimeSettings.Create().ResolveEffectiveValues;

    [Test]
    public void GetPendingFields_WhenAnUnsetSwitchIsSavedAsItsDefault_IsEmpty()
    {
        // A node started with developmentEnabled unset (effective true) whose page save stores true has nothing pending.
        var sut = new PendingRestartRegistry(Boot, Resolve);

        AssertEx.Empty(sut.GetPendingFields(Boot with
        {
            DevelopmentEnabled = new DevelopmentOptions().Enabled
        }));
    }

    [Test]
    public void GetPendingFields_WhenAnUnsetSwitchIsSavedAgainstItsDefault_NamesIt()
    {
        var sut = new PendingRestartRegistry(Boot, Resolve);

        AssertEx.Equal("developmentEnabled", string.Join(',', sut.GetPendingFields(Boot with
        {
            DevelopmentEnabled = !new DevelopmentOptions().Enabled
        })));
    }

    [Test]
    public void GetPendingFields_WhenNothingChanged_IsEmpty()
    {
        var sut = new PendingRestartRegistry(Boot, Resolve);

        AssertEx.Empty(sut.GetPendingFields(Boot with
        {
        }));
    }

    [Test]
    public void GetPendingFields_WhenAGatedFieldChanged_NamesItsWireField()
    {
        var sut = new PendingRestartRegistry(Boot, Resolve);

        AssertEx.Equal("chatCacheReuse", string.Join(',', sut.GetPendingFields(Boot with
        {
            ChatCacheReuse = 512
        })));
    }

    /// <summary>The node-settings save applies the process caps and idle TTLs to the running supervisors, so none of them waits for a restart.</summary>
    [Test]
    public void GetPendingFields_WhenOnlyTheProcessCapsAndTtlsChanged_IsEmpty()
    {
        var sut = new PendingRestartRegistry(Boot, Resolve);

        AssertEx.Empty(sut.GetPendingFields(Boot with
        {
            LlamaMaxLoadedProcesses = 4,
            LlamaIdleTimeToLiveSeconds = 60,
            ImageMaxLoadedProcesses = 2,
            ImageIdleTimeToLiveSeconds = 60
        }));
    }

    [Test]
    public void GetPendingFields_WhenOnlyALiveFieldChanged_IsEmpty()
    {
        var sut = new PendingRestartRegistry(Boot, Resolve);

        AssertEx.Empty(sut.GetPendingFields(Boot with
        {
            AgentHomeRunRetentionDays = 9
        }));
    }

    [Test]
    public void GetPendingFields_WhenTheDefaultModelChanged_IsEmpty()
    {
        // The node writes its own default after boot (first-run provisioning, the ext: reconciler) and reads it per send,
        // so a changed default is live and must not raise the pending-restart banner.
        var sut = new PendingRestartRegistry(Boot, Resolve);

        AssertEx.Empty(sut.GetPendingFields(Boot with
        {
            DefaultModelName = "provisioned-chat"
        }));
    }

    [Test]
    public void GetPendingFields_WhenTheBootValueIsRestored_ClearsTheField()
    {
        var sut = new PendingRestartRegistry(Boot, Resolve);
        var changed = Boot with
        {
            KvCacheType = "q8_0"
        };
        AssertEx.Equal("kvCacheType", string.Join(',', sut.GetPendingFields(changed)));

        AssertEx.Empty(sut.GetPendingFields(changed with
        {
            KvCacheType = "f16"
        }));
    }

    [Test]
    public void GetPendingFields_WhenTwoGatedFieldsChanged_NamesBothInOrdinalOrder()
    {
        var sut = new PendingRestartRegistry(Boot, Resolve);

        var pending = sut.GetPendingFields(Boot with
        {
            ChatCacheReuse = 512,
            KvCacheType = "q4_0"
        });

        AssertEx.Equal("chatCacheReuse,kvCacheType", string.Join(',', pending));
    }
}
