namespace XE_Local_AI_Engine.Tests.Auth;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Runs a one-shot hook once the SQL command that follows the next refresh-token-table command has finished.</summary>
/// <remarks>
///     A read counts as finished once its rows are consumed, a write once it executed; the hook gets whether that command
///     ran inside an open transaction. A revoke of every token and session issues one command per table, so the hook lands
///     after both, before anything later: a competing write slots in there without any timing.
/// </remarks>
internal sealed class RefreshTokenCommandHookInterceptor : DbCommandInterceptor
{
    private const string RefreshTokenTable = "node_refresh_tokens";
    private readonly StrongBox<Func<bool, Task>?> _hook;
    private int _sawRefreshTokenCommand;

    public RefreshTokenCommandHookInterceptor(StrongBox<Func<bool, Task>?> hook)
    {
        _hook = hook;
    }

    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
        DataReaderClosingEventData eventData,
        InterceptionResult result)
    {
        await OnCommandFinishedAsync(command);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await OnCommandFinishedAsync(command);
        return result;
    }

    private async Task OnCommandFinishedAsync(DbCommand command)
    {
        if (Volatile.Read(ref _hook.Value) is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _sawRefreshTokenCommand, 0, 0) == 1 && Interlocked.Exchange(ref _hook.Value, null) is { } hook)
        {
            await hook(command.Transaction is not null);
        }
        else if (command.CommandText.Contains(RefreshTokenTable, StringComparison.Ordinal))
        {
            _ = Interlocked.Exchange(ref _sawRefreshTokenCommand, 1);
        }
    }
}
