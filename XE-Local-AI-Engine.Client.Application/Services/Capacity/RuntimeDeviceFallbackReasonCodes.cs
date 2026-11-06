namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>Stable codes for why the selected runtime fell back to the CPU; the operator UI localizes its sentence from them.</summary>
public static class RuntimeDeviceFallbackReasonCodes
{
    /// <summary>A GPU is present, but the CPU llama.cpp variant is selected.</summary>
    public const string CpuVariant = "cpu_variant";

    /// <summary>A GPU variant is selected, ran its device listing, and enumerated no GPU devices.</summary>
    public const string ZeroDevices = "zero_devices";
}
