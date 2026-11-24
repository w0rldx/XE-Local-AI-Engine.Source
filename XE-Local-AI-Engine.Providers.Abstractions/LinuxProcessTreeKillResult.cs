// The Desktop shell compiles this file as linked source under its own namespace (it has no engine references).

#if XE_DESKTOP
namespace XE_Local_AI_Engine.Desktop.Linux;
#else
namespace XE_Local_AI_Engine.Providers.Abstractions;
#endif

/// <summary>What one <see cref="LinuxProcessTreeKill.Kill(System.Diagnostics.Process)" /> achieved.</summary>
public enum LinuxProcessTreeKillResult
{
    /// <summary>The root was already dead, or every descendant found was verified, stopped and accepted its SIGKILL through its pidfd.</summary>
    Complete,

    /// <summary>The runtime's own tree kill ran without an error: off Linux, or on Linux when the root could not be pinned or verified.</summary>
    FallbackKilled,

    /// <summary>A descendant could not be verified, pinned or confirmed stopped, or the time bound ran out: survivors are possible.</summary>
    Incomplete,

    /// <summary>A pinned member refused its SIGKILL, so it may be left stopped.</summary>
    KillFailed
}
