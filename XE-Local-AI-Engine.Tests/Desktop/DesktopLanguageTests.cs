namespace XE_Local_AI_Engine.Tests.Desktop;

using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The shell follows the SPA's <c>i18nextLng</c> choice. <c>[NotInParallel]</c> because
///     <see cref="DesktopText.Language" /> is process-wide state these tests change and restore.</summary>
[Category(TestCategories.Unit)]
[NotInParallel]
public sealed class DesktopLanguageTests
{
    [Test]
    [Arguments("\"de\"", "de")]
    [Arguments("de-DE", "de")]
    [Arguments("de_AT", "de")]
    [Arguments("en", "en")]
    [Arguments(" \"EN\" ", "en")]
    [Arguments("fr", "en")]
    public void Normalize_MapsSpaValuesToSupportedLanguages(string raw, string expected) =>
        AssertEx.Equal(expected, DesktopText.Normalize(raw));

    [Test]
    [Arguments("null")]
    [Arguments("\"\"")]
    [Arguments("")]
    [Arguments("  ")]
    [Arguments(null)]
    public void Normalize_ReturnsNullWhenNothingIsKnown(string? raw) => AssertEx.Null(DesktopText.Normalize(raw));

    [Test]
    public void Apply_ChangesOnlyOnARealSwitchAndRelabelsTheShell()
    {
        var previous = DesktopText.Language;
        try
        {
            DesktopText.Apply("en");
            AssertEx.False(DesktopText.Apply("\"en\""));
            AssertEx.False(DesktopText.Apply(null));
            AssertEx.False(DesktopText.Apply("null"));
            AssertEx.Equal("Keep running in the tray", DesktopText.KeepInTray);

            AssertEx.True(DesktopText.Apply("\"de\""));
            AssertEx.Equal("de", DesktopText.Language);
            AssertEx.Equal("Im Infobereich weiterlaufen", DesktopText.KeepInTray);
            AssertEx.False(DesktopText.Apply("de-DE"));

            AssertEx.True(DesktopText.Apply("en"));
            AssertEx.Equal("Keep running in the tray", DesktopText.KeepInTray);
        }
        finally
        {
            DesktopText.Apply(previous);
        }
    }

    [Test]
    public async Task LanguagePreference_RoundTripsAndIsNullWhenMissing()
    {
        using var directory = new TempDirectory();
        AssertEx.Null(await DesktopPreferences.ReadLanguageAsync(directory.Path, CancellationToken.None));

        await DesktopPreferences.WriteLanguageAsync(directory.Path, "de", CancellationToken.None);
        AssertEx.Equal("de", await DesktopPreferences.ReadLanguageAsync(directory.Path, CancellationToken.None));
        await DesktopPreferences.WriteLanguageAsync(directory.Path, "en", CancellationToken.None);
        AssertEx.Equal("en", await DesktopPreferences.ReadLanguageAsync(directory.Path, CancellationToken.None));
        AssertEx.False(File.Exists(Path.Combine(directory.Path, "desktop-language.txt.tmp")));
    }
}
