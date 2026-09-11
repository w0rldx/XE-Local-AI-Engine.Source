namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The sentinel a masked secret is replaced with. Pinned as a literal here because three surfaces state it — the
///     mask-out, the keep-on-write and the SPA — and a mask the write side does not recognise silently stores the
///     placeholder as the application's password.
/// </summary>
public sealed class ExternalAppVariableMaskTests
{
    [Test]
    public void Value_IsTheAgreedSentinel()
    {
        AssertEx.Equal("__XE_EXTERNAL_APP_SECRET_UNCHANGED__", ExternalAppVariableMask.Value);
    }

    /// <summary>
    ///     The two masks are separate on purpose: an MCP server's environment and an application's variables are
    ///     different round-trips, and one shared sentinel would make a value pasted from one form mean "unchanged" in
    ///     the other.
    /// </summary>
    [Test]
    public void Value_IsNotTheMcpEnvironmentSentinel()
    {
        AssertEx.NotEqual(McpEnvironmentMask.Value, ExternalAppVariableMask.Value);
    }
}
