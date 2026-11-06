namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>Stable codes for why the inference backend could not be determined; the operator UI localizes its detail from them.</summary>
public static class RuntimeDeviceUndeterminedReasonCodes
{
    /// <summary>No llama.cpp runtime is installed yet, so no device listing has run.</summary>
    public const string RuntimeMissing = "runtime_missing";

    /// <summary>The runtime is installed, but some of its CUDA companion libraries are missing.</summary>
    public const string CompanionLibrariesIncomplete = "companion_libraries_incomplete";

    /// <summary>The device listing ran but did not complete (rejected override, unloadable libraries, overrun probe).</summary>
    public const string ProbeFailed = "probe_failed";
}
