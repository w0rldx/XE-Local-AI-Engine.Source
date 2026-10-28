namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The sentinel a masked secret is replaced with, pinned as a literal because three surfaces state it (mask-out,
///     keep-on-write, the SPA) and a mask the write side misses silently stores the placeholder as the password.
/// </summary>
[Category(TestCategories.Unit)]
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
