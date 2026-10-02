namespace XE_Local_AI_Engine.Tests.Testing.Builders;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

/// <summary>
///     The real <see cref="NodeRuntimeSettings" /> over a substitute store, so a consumer test proves both the configuration seed
///     and a stored value. The stored record is read through a delegate, so a test can change it between calls.
/// </summary>
internal static class SeededNodeRuntimeSettings
{
    public static INodeRuntimeSettings Create(IDictionary<string, string?>? seed = null, Func<StoredNodeSettings>? stored = null)
    {
        var read = stored ?? (static () => new StoredNodeSettings());
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => read());
        store.Load(Arg.Any<CancellationToken>()).Returns(_ => read());

        return new NodeRuntimeSettings(store,
            new ConfigurationBuilder().AddInMemoryCollection(seed ?? new Dictionary<string, string?>()).Build(),
            Options.Create(new LocalChatAgentOptions()),
            Options.Create(new AgentHomeOptions()),
            Options.Create(new WorkerNodeOptions
            {
                NodeName = "test-node"
            }),
            new Lazy<IModelTrustResolver>(Substitute.For<IModelTrustResolver>()));
    }

    /// <summary>One switch's configuration seed and nothing stored.</summary>
    public static INodeRuntimeSettings FromSeed(string key, bool value) =>
        Create(new Dictionary<string, string?>
        {
            [key] = value ? "true" : "false"
        });
}
