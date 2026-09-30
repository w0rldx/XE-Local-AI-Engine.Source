namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>The exit code and captured stdout of a short command. A timeout or a failed start reports exit code -1 and empty output.</summary>
internal sealed record ProcessCaptureResult(int ExitCode, string Stdout);
