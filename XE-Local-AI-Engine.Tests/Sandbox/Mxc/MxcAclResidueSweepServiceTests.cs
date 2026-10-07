namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="MxcAclResidueSweepService" />: host start waits for the sweep, so no MXC launch can overlap it and lose a live grant, and
///     a failing sweep never fails the host.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class MxcAclResidueSweepServiceTests
{
    [Test]
    public async Task StartAsync_DoesNotCompleteBeforeTheSweepHasRun()
    {
        using var release = new ManualResetEventSlim();
        var swept = false;
        var service = new MxcAclResidueSweepService(NullLogger<MxcAclResidueSweepService>.Instance, () =>
        {
            release.Wait();
            swept = true;
        });

        var start = service.StartAsync(CancellationToken.None);

        AssertEx.False(start.IsCompleted, "host start must wait for the sweep");
        release.Set();
        await start;
        AssertEx.True(swept);
    }

    [Test]
    public async Task StartAsync_ASweepThatThrows_DoesNotFailTheHost()
    {
        var ran = false;
        var service = new MxcAclResidueSweepService(NullLogger<MxcAclResidueSweepService>.Instance, () =>
        {
            ran = true;
            throw new UnauthorizedAccessException("denied");
        });

        await service.StartAsync(CancellationToken.None);

        AssertEx.True(ran, "the sweep ran and its failure was absorbed");
    }
}
