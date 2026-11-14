namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Records the text of every query EF sends, so a test can assert which columns a read actually selects: a
///     projection that skips the encrypted columns never loads, and so never decrypts, them.
/// </summary>
internal sealed class RecordingCommandInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _queries = new();

    public IReadOnlyList<string> Queries => [.. _queries];

    public void Clear() => _queries.Clear();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _queries.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
