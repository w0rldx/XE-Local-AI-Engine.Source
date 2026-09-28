namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

using System.Data.Common;

/// <summary>
///     A freshly opened node SQLite connection whose pragma <c>Microsoft.Data.Sqlite</c> could not even prepare; a retry
///     loop treats it as transient.
/// </summary>
/// <remarks>
///     The inner exception is the <see cref="ArgumentOutOfRangeException" /> that masks a native prepare failure (SQLITE_MISUSE
///     or NOMEM leaves the statement tail unset). The caller disposes the connection. Background:
///     docs/wiki/08-data-and-persistence.md ("Connection pragmas").
/// </remarks>
public sealed class NodeSqliteTransientOpenException : DbException
{
    public NodeSqliteTransientOpenException()
    {
    }

    public NodeSqliteTransientOpenException(string message) : base(message)
    {
    }

    public NodeSqliteTransientOpenException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
