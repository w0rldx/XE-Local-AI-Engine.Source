namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>The production policy: the <c>ExecutionPreviewsEnabled</c> node setting (stored &gt; <c>ExecutionPreviews:Enabled</c> &gt; off).</summary>
internal sealed class NodeSettingsExecutionPreviewPolicy : IExecutionPreviewPolicy
{
    private readonly INodeRuntimeSettings _runtimeSettings;

    public NodeSettingsExecutionPreviewPolicy(INodeRuntimeSettings runtimeSettings)
    {
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    }

    public bool PreviewMechanismsEnabled => _runtimeSettings.GetExecutionPreviewsEnabled();
}
