namespace XE_Local_AI_Engine.Tests.Desktop;

using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The command overlay's chord parser, its persisted preferences and its show/hide decision.</summary>
[Category(TestCategories.Unit)]
public sealed class DesktopCommandOverlayTests
{
    [Test]
    [Arguments("Ctrl+Alt+Space", "Ctrl+Alt+Space", 0x3u, 0x20u)]
    [Arguments(" alt + ctrl + space ", "Ctrl+Alt+Space", 0x3u, 0x20u)]
    [Arguments("Control+Shift+K", "Ctrl+Shift+K", 0x6u, 0x4Bu)]
    [Arguments("Win+7", "Win+7", 0x8u, 0x37u)]
    [Arguments("Shift+F12", "Shift+F12", 0x4u, 0x7Bu)]
    [Arguments("Ctrl+Windows+F1", "Ctrl+Win+F1", 0xAu, 0x70u)]
    [Arguments("alt+f", "Alt+F", 0x1u, 0x46u)]
    public void Parse_AcceptsModifiersAndOneKey_AndPrintsTheCanonicalChord(string text, string canonical, uint modifiers, uint virtualKey)
    {
        var hotkey = AssertEx.NotNull(DesktopHotkey.Parse(text));

        AssertEx.Equal(canonical, hotkey.ToString());
        AssertEx.Equal(modifiers, hotkey.Modifiers);
        AssertEx.Equal(virtualKey, hotkey.VirtualKey);
        AssertEx.Equal(canonical, DesktopHotkey.Parse(hotkey.ToString())!.ToString());
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("Space")]
    [Arguments("Ctrl+")]
    [Arguments("Ctrl+Ctrl+A")]
    [Arguments("Ctrl+Alt")]
    [Arguments("Hyper+A")]
    [Arguments("Ctrl+F25")]
    [Arguments("Ctrl+F0")]
    [Arguments("Ctrl+F01")]
    [Arguments("Ctrl+F+1")]
    [Arguments("Ctrl+Enter")]
    [Arguments("Ctrl+AB")]
    public void Parse_RejectsMalformedChords_AndTheDefaultReplacesThem(string? text)
    {
        AssertEx.Null(DesktopHotkey.Parse(text));
        AssertEx.Equal(DesktopHotkey.DefaultChord, DesktopHotkey.ParseOrDefault(text).ToString());
    }

    [Test]
    public async Task HotkeyPreference_IsTheDefaultWhenAbsentOrMalformed_AndTheOperatorsChordOtherwise()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "desktop-hotkey.txt");
        AssertEx.Equal("Ctrl+Alt+Space", (await DesktopPreferences.ReadHotkeyAsync(directory.Path, CancellationToken.None)).ToString());

        await File.WriteAllTextAsync(path, "Ctrl+Shift+J\n");
        AssertEx.Equal("Ctrl+Shift+J", (await DesktopPreferences.ReadHotkeyAsync(directory.Path, CancellationToken.None)).ToString());

        await File.WriteAllTextAsync(path, "Shift");
        AssertEx.Equal("Ctrl+Alt+Space", (await DesktopPreferences.ReadHotkeyAsync(directory.Path, CancellationToken.None)).ToString());
    }

    [Test]
    public async Task OverlayBoundsPreference_RoundTrips_AndIsNullWhenMissingOrMalformed()
    {
        using var directory = new TempDirectory();
        AssertEx.Null(await DesktopPreferences.ReadOverlayBoundsAsync(directory.Path, CancellationToken.None));

        await DesktopPreferences.WriteOverlayBoundsAsync(directory.Path, new DesktopOverlayBounds(-1200, 340, 600.5, 240), CancellationToken.None);
        var bounds = AssertEx.NotNull(await DesktopPreferences.ReadOverlayBoundsAsync(directory.Path, CancellationToken.None));

        AssertEx.Equal(-1200, bounds.X);
        AssertEx.Equal(340, bounds.Y);
        AssertEx.Equal(600.5, bounds.Width);
        AssertEx.Equal(240.0, bounds.Height);
        AssertEx.False(File.Exists(Path.Combine(directory.Path, "desktop-overlay.txt.tmp")));

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "desktop-overlay.txt"), "10,20,wide,240");
        AssertEx.Null(await DesktopPreferences.ReadOverlayBoundsAsync(directory.Path, CancellationToken.None));
    }

    [Test]
    [Arguments("1,2,3")]
    [Arguments("1,2,3,4,5")]
    [Arguments("1.5,2,520,220")]
    [Arguments("1,2,NaN,220")]
    [Arguments("1,2,520,Infinity")]
    public void OverlayBounds_RejectsMalformedText(string text) =>
        AssertEx.Null(DesktopOverlayBounds.Parse(text));

    [Test]
    public void OverlayBounds_ClampsAnUnusableSize()
    {
        var bounds = AssertEx.NotNull(DesktopOverlayBounds.Parse(" 5 , 6 , 10 , 99999 "));

        AssertEx.Equal(DesktopOverlayBounds.MinWidth, bounds.Width);
        AssertEx.Equal(4096.0, bounds.Height);
        AssertEx.Equal("5,6,320,4096", bounds.ToString());
    }

    [Test]
    [Arguments(false, true, true)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    public void ShouldShow_TheHotkeyTogglesAndTheMenuAlwaysShows(bool visible, bool toggle, bool expected) =>
        AssertEx.Equal(expected, DesktopCommandWindow.ShouldShow(visible, toggle));

    [Test]
    [Arguments("http://127.0.0.1:5000/login?redirect=%2Fcommand", true)]
    [Arguments("http://127.0.0.1:5000/", true)]
    [Arguments("http://127.0.0.1:5000/command", false)]
    [Arguments("http://127.0.0.1:5000/command?x=1", false)]
    [Arguments(null, false)]
    public void ShouldReloadOnShow_OnlyWhenThePageLeftTheOverlayRoute(string? current, bool expected) =>
        AssertEx.Equal(expected, DesktopCommandWindow.ShouldReloadOnShow(current is null ? null : new Uri(current), new Uri("http://127.0.0.1:5000" + DesktopCommandWindow.Route)));
}
