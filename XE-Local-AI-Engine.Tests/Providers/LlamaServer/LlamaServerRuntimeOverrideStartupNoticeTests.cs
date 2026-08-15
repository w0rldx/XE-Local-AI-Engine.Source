namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the bring-your-own llama-server startup notice. The rule it encodes is asymmetric on purpose: an
///     active override skips SHA verification and must therefore be loud, while an unset override must produce
///     byte-identical (silent) startup output, so a normal pinned-acquisition deploy is not changed by this service
///     existing.
/// </summary>
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
