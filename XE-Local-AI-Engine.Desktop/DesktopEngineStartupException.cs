namespace XE_Local_AI_Engine.Desktop;

/// <summary>The owned engine exited before readiness; carries its exit code so the shell can exit with it.</summary>
internal sealed class DesktopEngineStartupException : InvalidOperationException
{
    internal DesktopEngineStartupException(string message, int? exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }

    /// <summary>The engine's exit code, or <see langword="null" /> when it was not observed.</summary>
    internal int? ExitCode { get; }
}
