namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Controls whether streamed child-process output is still part of the startup diagnostic window.</summary>
internal sealed class LlamaServerDiagnosticVerbosityWindow
{
    private volatile bool _serving;

    public Func<bool> IsServing => () => _serving;

    public void MarkServing()
    {
        _serving = true;
    }
}
