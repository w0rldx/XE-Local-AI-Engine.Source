namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The bring-your-own llama-server startup notice is asymmetric on purpose: an active override skips SHA
///     verification and must be loud, while an unset override leaves startup output byte-identical (silent).
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LlamaServerRuntimeOverrideStartupNoticeTests
{
    [Test]
    public async Task StartAsync_WhenTheOverrideIsActive_WarnsWithThePathAndVariant()
    {
        var logger = new RecordingLogger<LlamaServerRuntimeOverrideStartupNotice>();
        var options = new LlamaServerRuntimeOverrideOptions
        {
            ServerPath = "/opt/llama.cpp/build/bin/llama-server",
            Variant = GpuVariant.Cuda
        };
        var notice = new LlamaServerRuntimeOverrideStartupNotice(options, logger);

        await notice.StartAsync(CancellationToken.None);

        AssertEx.ContainsSingle(logger.Entries, entry => entry.Level == LogLevel.Warning);
        var message = logger.Entries[0].Message;
        AssertEx.Contains(message, "/opt/llama.cpp/build/bin/llama-server");
        AssertEx.Contains(message, "Cuda");
        AssertEx.Contains(message, "integrity hash verification is skipped");
    }

    [Test]
    public async Task StartAsync_WhenNoOverrideIsConfigured_SaysNothing()
    {
        var logger = new RecordingLogger<LlamaServerRuntimeOverrideStartupNotice>();
        var notice = new LlamaServerRuntimeOverrideStartupNotice(new LlamaServerRuntimeOverrideOptions(), logger);

        await notice.StartAsync(CancellationToken.None);

        AssertEx.Empty(logger.Entries);
    }

    [Test]
    public async Task StopAsync_SaysNothing()
    {
        var logger = new RecordingLogger<LlamaServerRuntimeOverrideStartupNotice>();
        var notice = new LlamaServerRuntimeOverrideStartupNotice(new LlamaServerRuntimeOverrideOptions
        {
            ServerPath = "/opt/llama.cpp/build/bin/llama-server"
        }, logger);

        await notice.StopAsync(CancellationToken.None);

        AssertEx.Empty(logger.Entries);
    }
}
