namespace XE_Local_AI_Engine.Desktop;

using System.Globalization;

/// <summary>The command overlay's remembered placement: position in screen pixels, size in device-independent units.</summary>
internal sealed class DesktopOverlayBounds
{
    internal const double DefaultWidth = 520;
    internal const double DefaultHeight = 220;
    internal const double MinWidth = 320;
    internal const double MinHeight = 160;
    private const double MaxSize = 4096;

    internal DesktopOverlayBounds(int x, int y, double width, double height)
    {
        X = x;
        Y = y;
        Width = Math.Clamp(width, MinWidth, MaxSize);
        Height = Math.Clamp(height, MinHeight, MaxSize);
    }

    internal int X { get; }
    internal int Y { get; }
    internal double Width { get; }
    internal double Height { get; }

    /// <summary>Reads "x,y,width,height" in invariant culture; null for anything malformed, a size out of range is clamped.</summary>
    internal static DesktopOverlayBounds? Parse(string? text)
    {
        var parts = text?.Trim().Split(',', StringSplitOptions.TrimEntries) ?? [];
        return parts.Length == 4
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
               && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
               && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
               && double.IsFinite(width) && double.IsFinite(height)
            ? new DesktopOverlayBounds(x, y, width, height)
            : null;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");
}
