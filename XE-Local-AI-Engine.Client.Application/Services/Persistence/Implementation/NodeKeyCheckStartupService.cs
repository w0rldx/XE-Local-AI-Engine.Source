namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Stops startup when the node key does not match the database, before any other hosted service reads or writes
///     an encrypted row.
/// </summary>
/// <remarks>
///     Runs in <see cref="StartAsync" />, after the startup migrations and before Kestrel accepts a request, and is
///     registered ahead of every other hosted service. The check itself is
///     <see cref="NodeChatDbContext.VerifyOrRecordNodeKeyCheckAsync" />.
/// </remarks>
public sealed class NodeKeyCheckStartupService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public NodeKeyCheckStartupService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().VerifyOrRecordNodeKeyCheckAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
