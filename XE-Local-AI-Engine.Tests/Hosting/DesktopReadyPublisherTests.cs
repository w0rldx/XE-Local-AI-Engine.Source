namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class DesktopReadyPublisherTests
{
    [Test]
    public async Task Publish_WritesTheSameReadyContractAsTheRealHostLifecycle()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "xe-ready-publisher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var output = new StringWriter();

            DesktopReadyPublisher.Publish(dataDirectory, "http://127.0.0.1:41234/", "1.2.3", TimeProvider.System, output, NullLogger.Instance);

            AssertEx.Equal($"XE_READY=1 XE_VERSION=1.2.3 XE_URL=http://127.0.0.1:41234 XE_MCP_URL=http://127.0.0.1:41234/api/local/v1/mcp/server XE_DATA_DIR={dataDirectory}{Environment.NewLine}",
                output.ToString());
            var ready = AssertEx.NotNull(await DesktopPortStore.ReadReadyAsync(dataDirectory));
            AssertEx.Equal("1.2.3", ready.Version);
            AssertEx.Equal("http://127.0.0.1:41234", ready.Url);
            AssertEx.Equal("http://127.0.0.1:41234/api/local/v1/mcp/server", ready.McpUrl);
            AssertEx.Equal(dataDirectory, ready.DataDir);
            AssertEx.Equal(Environment.ProcessId, ready.Pid);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
