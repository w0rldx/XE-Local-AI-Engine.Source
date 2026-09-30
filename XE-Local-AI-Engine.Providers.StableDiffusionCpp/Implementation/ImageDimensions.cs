namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Runtime.InteropServices;

/// <summary>The pixel dimensions declared by a PNG's IHDR header.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct ImageDimensions(int Width, int Height);
