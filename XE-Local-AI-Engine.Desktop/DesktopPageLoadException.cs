namespace XE_Local_AI_Engine.Desktop;

/// <summary>The start page could not load from the engine before the retry window ran out; WebView2 itself works.</summary>
internal sealed class DesktopPageLoadException : InvalidOperationException
{
    internal DesktopPageLoadException(string message)
        : base(message)
    {
    }
}
