namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Raw-ADO <see cref="IGeneratedImageRowStore" />, scoped to the DbContext lifetime.
/// </summary>
/// <remarks>
///     Raw SQL rather than EF because the row carries no encrypted column, matching the uploaded-file rows beside it:
///     there is nothing for the save-changes interceptor to do, so tracking the row would buy nothing.
/// </remarks>
public sealed class GeneratedImageRowStore : IGeneratedImageRowStore
{
    private readonly NodeChatDbContext _dbContext;

    public GeneratedImageRowStore(NodeChatDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task InsertAsync(GeneratedImageRow row, string storagePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              INSERT INTO generated_images (image_id, job_id, mime_type, width, height, size_bytes, storage_path, created_at_utc)
                              VALUES ($image_id, $job_id, $mime_type, $width, $height, $size_bytes, $storage_path, $created_at_utc);
                              """;
        AddParameter(command, "$image_id", row.ImageId);
        AddParameter(command, "$job_id", row.JobId);
        AddParameter(command, "$mime_type", row.MimeType);
        AddParameter(command, "$width", row.Width);
        AddParameter(command, "$height", row.Height);
        AddParameter(command, "$size_bytes", row.SizeBytes);
        AddParameter(command, "$storage_path", storagePath);
        AddParameter(command, "$created_at_utc", row.CreatedAtUtc);
        await OpenIfNeededAsync(command.Connection, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GeneratedImageLocation?> FindAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT job_id, mime_type, width, height, storage_path
                              FROM generated_images
                              WHERE image_id = $image_id;
                              """;
        AddParameter(command, "$image_id", imageId);
        await OpenIfNeededAsync(command.Connection, cancellationToken);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new GeneratedImageLocation
        {
            JobId = Guid.Parse(reader.GetString(0)),
            MimeType = reader.GetString(1),
            Width = reader.GetInt32(2),
            Height = reader.GetInt32(3),
            StoragePath = reader.GetString(4)
        };
    }

    private static Task OpenIfNeededAsync(DbConnection? connection, CancellationToken cancellationToken)
    {
        // Open-if-needed AND apply the shared WAL/busy_timeout/synchronous pragmas on the open.
        return NodeSqlitePragmas.OpenAndConfigureAsync(connection, cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
