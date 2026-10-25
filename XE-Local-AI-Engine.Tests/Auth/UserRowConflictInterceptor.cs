namespace XE_Local_AI_Engine.Tests.Auth;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Makes the next N updates of the Identity user row lose a concurrency race, as a concurrent user write would.</summary>
/// <remarks>
///     Right before such an UPDATE runs, it rewrites the row's <c>ConcurrencyStamp</c> on the same connection and
///     transaction, so EF's optimistic check matches no row and Identity reports a concurrency failure, without timing.
/// </remarks>
internal sealed class UserRowConflictInterceptor : DbCommandInterceptor
{
    private readonly StrongBox<int> _remainingConflicts;

    public UserRowConflictInterceptor(StrongBox<int> remainingConflicts)
    {
        _remainingConflicts = remainingConflicts;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await ConflictIfUserUpdateAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await ConflictIfUserUpdateAsync(command, cancellationToken);
        return result;
    }

    private async Task ConflictIfUserUpdateAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!command.CommandText.Contains("UPDATE \"AspNetUsers\"", StringComparison.Ordinal)
            || Interlocked.Decrement(ref _remainingConflicts.Value) < 0)
        {
            return;
        }

        await using var bump = command.Connection!.CreateCommand();
        bump.Transaction = command.Transaction;
        bump.CommandText = "UPDATE \"AspNetUsers\" SET \"ConcurrencyStamp\" = lower(hex(randomblob(16)));";
        _ = await bump.ExecuteNonQueryAsync(cancellationToken);
    }
}
