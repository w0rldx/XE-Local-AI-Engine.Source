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
        LlamaMaxLoadedProcesses = 2,
        KvCacheType = "f16"
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

        AssertEx.Equal("llamaMaxLoadedProcesses", string.Join(',', sut.GetPendingFields(Boot with
        {
            LlamaMaxLoadedProcesses = 3
        })));
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
            LlamaMaxLoadedProcesses = 4,
            KvCacheType = "q4_0"
        });

        AssertEx.Equal("kvCacheType,llamaMaxLoadedProcesses", string.Join(',', pending));
    }
}
