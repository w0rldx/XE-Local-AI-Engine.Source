namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     AUD4-09: the size-aware readiness-timeout computation and the options validation added to
///     <see cref="LlamaServerSupervisorOptions" />.
/// </summary>
public sealed class LlamaServerSupervisorOptionsTests
{
    private const long BytesPerGiB = 1024L * 1024 * 1024;

    private static LlamaServerSupervisorOptions SizeAwareOptions()
    {
        return new LlamaServerSupervisorOptions
        {
            ReadinessBaseTimeout = TimeSpan.FromSeconds(120),
            ReadinessTimeoutModelSizeThresholdGiB = 4,
            ReadinessTimeoutSecondsPerGiB = 20,
            ReadinessTimeoutCap = TimeSpan.FromSeconds(600)
        };
    }

    [Test]
    public void ResolveReadinessTimeout_UnknownOrBelowThreshold_UsesBase()
    {
        var options = SizeAwareOptions();

        AssertEx.Equal(TimeSpan.FromSeconds(120), options.ResolveReadinessTimeout(0)); // unknown size → base
        AssertEx.Equal(TimeSpan.FromSeconds(120), options.ResolveReadinessTimeout(2 * BytesPerGiB)); // below threshold
        AssertEx.Equal(TimeSpan.FromSeconds(120), options.ResolveReadinessTimeout(4 * BytesPerGiB)); // at threshold
    }

    [Test]
    public void ResolveReadinessTimeout_AboveThreshold_ScalesLinearly()
    {
        var options = SizeAwareOptions();

        // 8 GiB → 120 + (8 - 4) * 20 = 200s.
        AssertEx.Equal(TimeSpan.FromSeconds(200), options.ResolveReadinessTimeout(8 * BytesPerGiB));

        // 10 GiB → 120 + (10 - 4) * 20 = 240s.
        AssertEx.Equal(TimeSpan.FromSeconds(240), options.ResolveReadinessTimeout(10 * BytesPerGiB));
    }

    [Test]
    public void ResolveReadinessTimeout_HugeModel_ClampedToCap()
    {
        var options = SizeAwareOptions();

        // 100 GiB → 120 + 96 * 20 = 2040s, clamped to the 600s cap.
        AssertEx.Equal(TimeSpan.FromSeconds(600), options.ResolveReadinessTimeout(100 * BytesPerGiB));
    }

    [Test]
    public void Validate_Defaults_Pass()
    {
        // The shipped defaults must be structurally valid.
        new LlamaServerSupervisorOptions().Validate();
    }

    [Test]
    public async Task Validate_NonPositiveReadinessBaseTimeout_Throws()
    {
        var options = new LlamaServerSupervisorOptions
        {
            ReadinessBaseTimeout = TimeSpan.Zero
        };

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Validate_CapBelowBase_Throws()
    {
        var options = new LlamaServerSupervisorOptions
        {
            ReadinessBaseTimeout = TimeSpan.FromSeconds(120),
            ReadinessTimeoutCap = TimeSpan.FromSeconds(60)
        };

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }

    [Test]
    public void HttpNetworkTimeoutDefault_IsNotShorterThanTheNodeMessageTimeoutCeiling()
    {
        // G9: this is the OUTERMOST floor against a wedged socket, not the per-turn bound. When it was 600s an operator
        // who raised the node "Maximum message request timeout" above 600s got a socket abort from the inner HTTP
        // timeout first, so the setting silently had no effect for local models. It must never be shorter than the node
        // setting's own ceiling (StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds = 3600; that constant lives in
        // Client.Application, which this provider project cannot reference — see the comment on the default).
        AssertEx.True(new LlamaServerSupervisorOptions().HttpNetworkTimeout >= TimeSpan.FromSeconds(3600),
            "the llama-server HTTP network timeout must not pre-empt the operator's maximum message request timeout.");
    }

    [Test]
    public async Task Validate_NonPositiveEjectDrainTimeout_Throws()
    {
        var options = new LlamaServerSupervisorOptions
        {
            EjectDrainTimeout = TimeSpan.Zero
        };

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }

    [Test]
    public void ComputeDefaultChatCacheRamMiB_ScalesWithRamAndClamps()
    {
        // One eighth of RAM, clamped to [512, 8192] MiB: floor on unknown/tiny, upstream default only at 64 GB+.
        AssertEx.Equal(expected: 512, LlamaServerSupervisorOptions.ComputeDefaultChatCacheRamMiB(0));
        AssertEx.Equal(expected: 512, LlamaServerSupervisorOptions.ComputeDefaultChatCacheRamMiB(2 * BytesPerGiB));
        AssertEx.Equal(expected: 2048, LlamaServerSupervisorOptions.ComputeDefaultChatCacheRamMiB(16 * BytesPerGiB));
        AssertEx.Equal(expected: 4096, LlamaServerSupervisorOptions.ComputeDefaultChatCacheRamMiB(32 * BytesPerGiB));
        AssertEx.Equal(expected: 8192, LlamaServerSupervisorOptions.ComputeDefaultChatCacheRamMiB(64 * BytesPerGiB));
        AssertEx.Equal(expected: 8192, LlamaServerSupervisorOptions.ComputeDefaultChatCacheRamMiB(256 * BytesPerGiB));
    }

    [Test]
    public async Task Validate_NegativeChatCacheRam_Throws()
    {
        var options = new LlamaServerSupervisorOptions
        {
            ChatCacheRamMiB = -1
        };

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Validate_NegativeReadinessRetries_Throws()
    {
        var options = new LlamaServerSupervisorOptions
        {
            MaxReadinessTimeoutRetries = -1
        };

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }
}
