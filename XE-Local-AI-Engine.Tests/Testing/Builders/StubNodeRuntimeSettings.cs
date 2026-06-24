namespace XE_Local_AI_Engine.Tests.Testing.Builders;

using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Builds a configured <see cref="INodeRuntimeSettings" /> substitute for tests of consumers that were repointed off
///     <c>IOptions&lt;T&gt;</c> onto the accessor (the appsettings-to-node-settings migration). Defaults mirror
///     the <c>StoredNodeSettings</c> seed defaults; each <c>With*</c> override sets a single migrated value so a test can
///     pin only the knob it asserts on.
/// </summary>
public sealed class StubNodeRuntimeSettings
{
    private int _agentHomeCommandTimeoutSeconds = StoredNodeSettings.DefaultAgentHomeCommandTimeoutSeconds;
    private long _agentHomeMaxPatchBytes = StoredNodeSettings.DefaultAgentHomeMaxPatchBytes;
    private long _agentHomeMaxSelectedFolderBytes = StoredNodeSettings.DefaultAgentHomeMaxSelectedFolderBytes;
    private int _agentHomePrepareTimeoutSeconds = StoredNodeSettings.DefaultAgentHomePrepareTimeoutSeconds;
    private string _defaultModelName = "qwen3.5:0.8b";
    private bool _enableTools = StoredNodeSettings.DefaultEnableTools;
    private long _huggingFaceDiskMarginBytes = StoredNodeSettings.DefaultHuggingFaceDiskMarginBytes;
    private string _huggingFaceDefaultQuant = StoredNodeSettings.DefaultHuggingFaceQuant;
    private int _llamaMaxLoadedProcesses = StoredNodeSettings.DefaultLlamaMaxLoadedProcesses;
    private int _maxPendingToolCallAgeMinutes = StoredNodeSettings.DefaultMaxPendingToolCallAgeMinutes;
    private int _maxResponseSizeMb = StoredNodeSettings.DefaultMaxResponseSizeMb;
    private int _orchestrationIdleTimeoutSeconds = StoredNodeSettings.DefaultOrchestrationIdleTimeoutSeconds;
    private IReadOnlyList<string> _toolCapableModels = ["qwen3:8b"];

    public static StubNodeRuntimeSettings Create()
    {
        return new StubNodeRuntimeSettings();
    }

    public StubNodeRuntimeSettings WithDefaultModelName(string defaultModelName)
    {
        ArgumentNullException.ThrowIfNull(defaultModelName);
        _defaultModelName = defaultModelName;
        return this;
    }

    public StubNodeRuntimeSettings WithEnableTools(bool enableTools)
    {
        _enableTools = enableTools;
        return this;
    }

    public StubNodeRuntimeSettings WithToolCapableModels(params string[] toolCapableModels)
    {
        ArgumentNullException.ThrowIfNull(toolCapableModels);
        _toolCapableModels = [.. toolCapableModels];
        return this;
    }

    public StubNodeRuntimeSettings WithHuggingFaceDefaultQuant(string huggingFaceDefaultQuant)
    {
        ArgumentNullException.ThrowIfNull(huggingFaceDefaultQuant);
        _huggingFaceDefaultQuant = huggingFaceDefaultQuant;
        return this;
    }

    public StubNodeRuntimeSettings WithHuggingFaceDiskMarginBytes(long huggingFaceDiskMarginBytes)
    {
        _huggingFaceDiskMarginBytes = huggingFaceDiskMarginBytes;
        return this;
    }

    public StubNodeRuntimeSettings WithLlamaMaxLoadedProcesses(int llamaMaxLoadedProcesses)
    {
        _llamaMaxLoadedProcesses = llamaMaxLoadedProcesses;
        return this;
    }

    public StubNodeRuntimeSettings WithMaxResponseSizeMb(int maxResponseSizeMb)
    {
        _maxResponseSizeMb = maxResponseSizeMb;
        return this;
    }

    public StubNodeRuntimeSettings WithMaxPendingToolCallAgeMinutes(int maxPendingToolCallAgeMinutes)
    {
        _maxPendingToolCallAgeMinutes = maxPendingToolCallAgeMinutes;
        return this;
    }

    public StubNodeRuntimeSettings WithOrchestrationIdleTimeoutSeconds(int orchestrationIdleTimeoutSeconds)
    {
        _orchestrationIdleTimeoutSeconds = orchestrationIdleTimeoutSeconds;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomePrepareTimeoutSeconds(int agentHomePrepareTimeoutSeconds)
    {
        _agentHomePrepareTimeoutSeconds = agentHomePrepareTimeoutSeconds;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeCommandTimeoutSeconds(int agentHomeCommandTimeoutSeconds)
    {
        _agentHomeCommandTimeoutSeconds = agentHomeCommandTimeoutSeconds;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeMaxSelectedFolderBytes(long agentHomeMaxSelectedFolderBytes)
    {
        _agentHomeMaxSelectedFolderBytes = agentHomeMaxSelectedFolderBytes;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeMaxPatchBytes(long agentHomeMaxPatchBytes)
    {
        _agentHomeMaxPatchBytes = agentHomeMaxPatchBytes;
        return this;
    }

    public INodeRuntimeSettings Build()
    {
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetDefaultModelNameAsync(Arg.Any<CancellationToken>()).Returns(_defaultModelName);
        settings.GetEnableToolsAsync(Arg.Any<CancellationToken>()).Returns(_enableTools);
        settings.GetToolCapableModelsAsync(Arg.Any<CancellationToken>()).Returns(_toolCapableModels);
        settings.GetHuggingFaceDefaultQuantAsync(Arg.Any<CancellationToken>()).Returns(_huggingFaceDefaultQuant);
        settings.GetHuggingFaceDiskMarginBytesAsync(Arg.Any<CancellationToken>()).Returns(_huggingFaceDiskMarginBytes);
        settings.GetLlamaMaxLoadedProcessesAsync(Arg.Any<CancellationToken>()).Returns(_llamaMaxLoadedProcesses);
        settings.GetLlamaIdleTimeToLiveAsync(Arg.Any<CancellationToken>())
                .Returns(TimeSpan.FromSeconds(StoredNodeSettings.DefaultLlamaIdleTimeToLiveSeconds));
        settings.GetMaxResponseSizeMbAsync(Arg.Any<CancellationToken>()).Returns(_maxResponseSizeMb);
        settings.GetRecommendedLlamaCppTagAsync(Arg.Any<CancellationToken>()).Returns(StoredNodeSettings.DefaultRecommendedLlamaCppTag);
        settings.GetOrchestrationIdleTimeoutSecondsAsync(Arg.Any<CancellationToken>()).Returns(_orchestrationIdleTimeoutSeconds);
        settings.GetAgentHomePrepareTimeoutSecondsAsync(Arg.Any<CancellationToken>()).Returns(_agentHomePrepareTimeoutSeconds);
        settings.GetAgentHomeCommandTimeoutSecondsAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeCommandTimeoutSeconds);
        settings.GetAgentHomeMaxSelectedFolderBytesAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeMaxSelectedFolderBytes);
        settings.GetAgentHomeMaxPatchBytesAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeMaxPatchBytes);
        settings.GetMaxPendingToolCallAgeMinutesAsync(Arg.Any<CancellationToken>()).Returns(_maxPendingToolCallAgeMinutes);
        settings.GetSamplingDefaultsAsync(Arg.Any<CancellationToken>()).Returns((SamplingOptions?)null);

        // Synchronous twins (composition/ctor path) must mirror the async values so consumers repointed onto the sync
        // getters (e.g. InvocationRunner, the DI factory seeds) observe the same configured knobs.
        settings.GetDefaultModelName().Returns(_defaultModelName);
        settings.GetToolCapableModels().Returns(_toolCapableModels);
        settings.GetOllamaEndpoint().Returns(StoredNodeSettings.DefaultOllamaEndpoint);
        settings.GetHuggingFaceDefaultQuant().Returns(_huggingFaceDefaultQuant);
        settings.GetHuggingFaceDiskMarginBytes().Returns(_huggingFaceDiskMarginBytes);
        settings.GetLlamaMaxLoadedProcesses().Returns(_llamaMaxLoadedProcesses);
        settings.GetLlamaIdleTimeToLive().Returns(TimeSpan.FromSeconds(StoredNodeSettings.DefaultLlamaIdleTimeToLiveSeconds));
        settings.GetMaxResponseSizeMb().Returns(_maxResponseSizeMb);
        settings.GetOrchestrationIdleTimeoutSeconds().Returns(_orchestrationIdleTimeoutSeconds);
        settings.GetMaxPendingToolCallAgeMinutes().Returns(_maxPendingToolCallAgeMinutes);
        return settings;
    }
}
