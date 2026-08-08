namespace XE_Local_AI_Engine.Tests.Endpoints;

using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The upload file-name sanitizer reduces a client-supplied name to a safe leaf so a traversal/control/reserved
///     value can never become display metadata or (defense in depth) influence a storage path.
/// </summary>
public sealed class UploadFileNameSanitizerTests
{
    [Test]
    public void ToSafeLeafFileName_WhenPlainName_ReturnsItUnchanged()
    {
        AssertEx.Equal("report.pdf", UploadFileNameSanitizer.ToSafeLeafFileName("report.pdf"));
    }

    [Test]
    public void ToSafeLeafFileName_WhenPosixTraversal_ReducesToLeaf()
    {
        AssertEx.Equal("passwd", UploadFileNameSanitizer.ToSafeLeafFileName("../../etc/passwd"));
    }

    [Test]
    public void ToSafeLeafFileName_WhenWindowsTraversal_ReducesToLeaf()
    {
        AssertEx.Equal("config.sys", UploadFileNameSanitizer.ToSafeLeafFileName("..\\..\\windows\\config.sys"));
    }

    [Test]
    public void ToSafeLeafFileName_WhenDotSegments_ReturnsNull()
    {
        AssertEx.Null(UploadFileNameSanitizer.ToSafeLeafFileName("."));
        AssertEx.Null(UploadFileNameSanitizer.ToSafeLeafFileName(".."));
    }

    [Test]
    public void ToSafeLeafFileName_WhenEmptyOrWhitespace_ReturnsNull()
    {
        AssertEx.Null(UploadFileNameSanitizer.ToSafeLeafFileName(null));
        AssertEx.Null(UploadFileNameSanitizer.ToSafeLeafFileName(string.Empty));
        AssertEx.Null(UploadFileNameSanitizer.ToSafeLeafFileName("   "));
    }

    [Test]
    public void ToSafeLeafFileName_WhenControlCharacters_ReturnsNull()
    {
        AssertEx.Null(UploadFileNameSanitizer.ToSafeLeafFileName("bad\u0000name.txt"));
    }
}
