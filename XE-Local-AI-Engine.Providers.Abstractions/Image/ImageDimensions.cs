namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

using System.Runtime.InteropServices;

/// <summary>The pixel dimensions declared by an image file header.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ImageDimensions(int Width, int Height);
