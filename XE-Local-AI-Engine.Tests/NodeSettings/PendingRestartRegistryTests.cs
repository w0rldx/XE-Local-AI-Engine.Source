namespace XE_Local_AI_Engine.Tests.NodeSettings;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

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

    [Test]
    public void GetPendingFields_WhenNothingChanged_IsEmpty()
    {
        var sut = new PendingRestartRegistry(Boot);

        AssertEx.Empty(sut.GetPendingFields(Boot with
        {
        }));
    }

    [Test]
    public void GetPendingFields_WhenAGatedFieldChanged_NamesItsWireField()
    {
        var sut = new PendingRestartRegistry(Boot);

        AssertEx.Equal("llamaMaxLoadedProcesses", string.Join(',', sut.GetPendingFields(Boot with
        {
            LlamaMaxLoadedProcesses = 3
        })));
    }

    [Test]
    public void GetPendingFields_WhenOnlyALiveFieldChanged_IsEmpty()
    {
        var sut = new PendingRestartRegistry(Boot);

        AssertEx.Empty(sut.GetPendingFields(Boot with
        {
            AgentHomeRunRetentionDays = 9
        }));
    }

    [Test]
    public void GetPendingFields_WhenTheBootValueIsRestored_ClearsTheField()
    {
        var sut = new PendingRestartRegistry(Boot);
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
        var sut = new PendingRestartRegistry(Boot);

        var pending = sut.GetPendingFields(Boot with
        {
            LlamaMaxLoadedProcesses = 4,
            KvCacheType = "q4_0"
        });

        AssertEx.Equal("kvCacheType,llamaMaxLoadedProcesses", string.Join(',', pending));
    }
}
