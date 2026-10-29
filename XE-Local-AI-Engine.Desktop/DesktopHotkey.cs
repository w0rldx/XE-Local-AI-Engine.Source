namespace XE_Local_AI_Engine.Desktop;

using System.Globalization;

/// <summary>A global key chord such as "Ctrl+Alt+Space", in the RegisterHotKey modifier and virtual-key encoding.</summary>
internal sealed class DesktopHotkey
{
    internal const string DefaultChord = "Ctrl+Alt+Space";

    private const uint Alt = 0x1;
    private const uint Control = 0x2;
    private const uint Shift = 0x4;
    private const uint Windows = 0x8;

    private DesktopHotkey(uint modifiers, uint virtualKey, string key)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        Key = key;
    }

    internal uint Modifiers { get; }
    internal uint VirtualKey { get; }
    private string Key { get; }

    internal static DesktopHotkey Default { get; } =
        Parse(DefaultChord) ?? throw new InvalidOperationException($"The default hotkey chord '{DefaultChord}' does not parse.");

    internal static DesktopHotkey ParseOrDefault(string? text) =>
        Parse(text) ?? Default;

    /// <summary>Accepts Ctrl, Alt, Shift and Win plus one key (A-Z, 0-9, F1-F24 or Space), case-insensitive; null for
    ///     anything else, including a chord without a modifier or with a repeated part.</summary>
    internal static DesktopHotkey? Parse(string? text)
    {
        var parts = text?.Split('+', StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length < 2)
        {
            return null;
        }

        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => Control,
                "ALT" => Alt,
                "SHIFT" => Shift,
                "WIN" or "WINDOWS" => Windows,
                _ => 0u
            };
            if (modifier == 0 || (modifiers & modifier) != 0)
            {
                return null;
            }

            modifiers |= modifier;
        }

        var key = parts[^1].ToUpperInvariant();
        uint? virtualKey = key switch
        {
            "SPACE" => 0x20,
            [var letter] when letter is >= 'A' and <= 'Z' => letter,
            [var digit] when digit is >= '0' and <= '9' => digit,
            ['F', .. var number] when int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var function)
                                      && function is >= 1 and <= 24 && number[0] != '0' => (uint)(0x6F + function),
            _ => null
        };
        if (virtualKey is null)
        {
            return null;
        }

        return new DesktopHotkey(modifiers, virtualKey.Value, key == "SPACE" ? "Space" : key);
    }

    /// <summary>The canonical text: modifiers in Ctrl, Alt, Shift, Win order, then the key.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & Control) != 0) { parts.Add("Ctrl"); }

        if ((Modifiers & Alt) != 0) { parts.Add("Alt"); }

        if ((Modifiers & Shift) != 0) { parts.Add("Shift"); }

        if ((Modifiers & Windows) != 0) { parts.Add("Win"); }

        parts.Add(Key);
        return string.Join('+', parts);
    }
}
