namespace XE_Local_AI_Engine.Desktop;

/// <summary>What the failure page says after a failed start, and whether it offers the WebView2 download.</summary>
internal readonly record struct StartupFailureNotice(string Text, bool IncludeWebViewLink);
