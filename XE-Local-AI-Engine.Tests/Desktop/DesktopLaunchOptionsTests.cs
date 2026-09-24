namespace XE_Local_AI_Engine.Tests.Desktop;

using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class DesktopLaunchOptionsTests
{
    [Test]
    public void Parse_AcceptsExplicitLoopbackOriginAndAbsoluteProfileDirectory()
    {
        var profileDirectory = Path.Combine(Path.GetTempPath(), "xe-desktop-profile");

        var options = DesktopLaunchOptions.Parse(["--profile-dir", profileDirectory, "--origin", "http://127.0.0.1:54321"]);

        AssertEx.Equal("http://127.0.0.1:54321/", options.Origin.AbsoluteUri);
        AssertEx.Equal(Path.GetFullPath(profileDirectory), options.ProfileDirectory);
    }

    [Test]
    public void Parse_RejectsOriginsThatAreNotPlainLoopbackOrigins()
    {
        AssertInvalidOrigin("https://127.0.0.1:54321");
        AssertInvalidOrigin("http://example.com:54321");
        AssertInvalidOrigin("http://user@127.0.0.1:54321");
        AssertInvalidOrigin("http://127.0.0.1:54321/path");
        AssertInvalidOrigin("http://127.0.0.1:54321/?token=secret");
        AssertInvalidOrigin("file:///tmp/index.html");
        AssertInvalidOrigin("http://127.0.0.1:0");
        AssertInvalidOrigin("http://127.0.0.1:54321/#secret");
    }

    [Test]
    public void Parse_RejectsMissingDuplicateAndUnknownArguments()
    {
        _ = AssertEx.Throws<ArgumentNullException>(() => DesktopLaunchOptions.Parse(null!));
        _ = AssertEx.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse([]));
        _ = AssertEx.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse(["--origin"]));
        _ = AssertEx.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse(["--origin", "http://127.0.0.1:54321", "--origin", "http://127.0.0.1:54321"]));
        _ = AssertEx.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse(["--origin", "http://127.0.0.1:54321", "--unknown", Path.GetTempPath()]));
    }

    [Test]
    public void Parse_RejectsRelativeProfileDirectory()
    {
        _ = AssertEx.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse(["--origin", "http://localhost:54321", "--profile-dir", "relative-profile"]));
    }

    [Test]
    public void ClassifyNavigation_AllowsSameOriginLaunchesHttpExternallyAndBlocksOtherSchemes()
    {
        var origin = new Uri("http://127.0.0.1:54321/");

        AssertEx.Equal(NavigationDisposition.SameOrigin,
            DesktopLaunchOptions.ClassifyNavigation(origin, new Uri("http://127.0.0.1:54321/chat/1")));
        AssertEx.Equal(NavigationDisposition.External,
            DesktopLaunchOptions.ClassifyNavigation(origin, new Uri("https://example.com/docs")));
        AssertEx.Equal(NavigationDisposition.Blocked,
            DesktopLaunchOptions.ClassifyNavigation(origin, new Uri("file:///tmp/index.html")));
        AssertEx.Equal(NavigationDisposition.Blocked,
            DesktopLaunchOptions.ClassifyNavigation(origin, new Uri("http://user@127.0.0.1:54321/")));
        AssertEx.Equal(NavigationDisposition.Blocked,
            DesktopLaunchOptions.ClassifyNavigation(origin, null));
        AssertEx.Equal(NavigationDisposition.Blocked,
            DesktopLaunchOptions.ClassifyNavigation(origin, new Uri("/chat", UriKind.Relative)));
        AssertEx.Equal(NavigationDisposition.Blocked,
            DesktopLaunchOptions.ClassifyNavigation(origin, new Uri("javascript:alert(1)")));
        AssertEx.Equal(NavigationDisposition.External,
            DesktopLaunchOptions.ClassifyNavigation(origin, new Uri("http://127.0.0.1:54322/")));
    }

    private static void AssertInvalidOrigin(string origin)
    {
        _ = AssertEx.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse(["--origin", origin, "--profile-dir", Path.GetTempPath()]));
    }
}
