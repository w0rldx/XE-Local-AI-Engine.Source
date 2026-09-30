namespace XE_Local_AI_Engine.Tests.Mcp;

using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Pins what the wire shows of an HTTP MCP server URL and when an update's masked URL means "keep the stored one".</summary>
[Category(TestCategories.Unit)]
public sealed class McpUrlMaskTests
{
    [Test]
    [Arguments("http://127.0.0.1:1/mcp", "http://127.0.0.1:1/mcp")]
    [Arguments("http://127.0.0.1:1/mcp?token=abc", "http://127.0.0.1:1/mcp?token=***")]
    [Arguments("http://127.0.0.1:1/mcp?a=1&b=&bare#frag", "http://127.0.0.1:1/mcp?a=***&b=***&***#frag")]
    [Arguments("http://user:pw@127.0.0.1:1/mcp", "http://***@127.0.0.1:1/mcp")]
    public async Task Mask_HidesUserInfoAndEveryQueryValue(string url, string expected)
    {
        AssertEx.Equal(expected, McpUrlMask.Mask(url));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Restore_KeepsTheStoredUrlOnlyWhenTheMaskedFormCameBack()
    {
        const string stored = "http://127.0.0.1:1/mcp?token=abc";

        AssertEx.Equal(stored, McpUrlMask.Restore("http://127.0.0.1:1/mcp?token=***", stored));
        AssertEx.Equal("http://127.0.0.1:1/mcp?token=new", McpUrlMask.Restore("http://127.0.0.1:1/mcp?token=new", stored));
        AssertEx.Equal("http://127.0.0.1:1/other", McpUrlMask.Restore("http://127.0.0.1:1/other", stored));
        AssertEx.Equal("http://127.0.0.1:1/mcp", McpUrlMask.Restore("http://127.0.0.1:1/mcp", "http://127.0.0.1:1/mcp"));
        AssertEx.Null(McpUrlMask.Restore(incoming: null, stored));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Restore_WhenTheRestOfAMaskedUrlWasEdited_RestoresEachMaskedValueByKey()
    {
        // Editing the port of a masked URL stored the literal "***" as the token.
        const string stored = "http://127.0.0.1:1/mcp?token=abc&mode=x";

        AssertEx.Equal("http://127.0.0.1:2/mcp?token=abc&mode=y", McpUrlMask.Restore("http://127.0.0.1:2/mcp?token=***&mode=y", stored));
        _ = AssertEx.Throws<McpServerValidationException>(() => McpUrlMask.Restore("http://127.0.0.1:2/mcp?other=***", stored));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Restore_ARepeatedQueryKey_RestoresEachOccurrenceInOrder_AndRefusesAnExtraOne()
    {
        // Codex review 2026-09-30: only the first value per key was kept, so a port edit turned scope=read&scope=write into read,read.
        const string stored = "http://127.0.0.1:1/mcp?scope=read&scope=write";

        AssertEx.Equal("http://127.0.0.1:2/mcp?scope=read&scope=write", McpUrlMask.Restore("http://127.0.0.1:2/mcp?scope=***&scope=***", stored));
        _ = AssertEx.Throws<McpServerValidationException>(() => McpUrlMask.Restore("http://127.0.0.1:2/mcp?scope=***&scope=***&scope=***", stored));
        await Task.CompletedTask;
    }
}
