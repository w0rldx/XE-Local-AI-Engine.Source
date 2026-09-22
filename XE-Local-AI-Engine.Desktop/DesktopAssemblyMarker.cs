namespace XE_Local_AI_Engine.Desktop;

using System.Reflection;

public static class DesktopAssemblyMarker
{
    public static Assembly Assembly { get; } = typeof(DesktopAssemblyMarker).Assembly;
}
