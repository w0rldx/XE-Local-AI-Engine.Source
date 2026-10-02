namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Data.Common;
using System.Globalization;
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
            JobId = await reader.IsDBNullAsync(0, cancellationToken) ? null : Guid.Parse(reader.GetString(0)),
            MimeType = reader.GetString(1),
            Width = reader.GetInt32(2),
            Height = reader.GetInt32(3),
            StoragePath = reader.GetString(4)
        };
    }

    public async Task<IReadOnlyList<GeneratedImageRow>> ListUploadsAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT image_id, mime_type, width, height, size_bytes, created_at_utc
                              FROM generated_images
                              WHERE job_id IS NULL
                              ORDER BY created_at_utc DESC, image_id DESC
                              LIMIT $limit OFFSET $offset;
                              """;
        // Floored: a negative LIMIT is "no limit" to SQLite, and a negative OFFSET is not a page.
        AddParameter(command, "$limit", Math.Max(val1: 0, limit));
        AddParameter(command, "$offset", Math.Max(val1: 0, offset));
        await OpenIfNeededAsync(command.Connection, cancellationToken);

        var rows = new List<GeneratedImageRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GeneratedImageRow
            {
                ImageId = Guid.Parse(reader.GetString(0)),
                JobId = null,
                MimeType = reader.GetString(1),
                Width = reader.GetInt32(2),
                Height = reader.GetInt32(3),
                SizeBytes = reader.GetInt64(4),
                CreatedAtUtc = reader.GetInt64(5)
            });
        }

        return rows;
    }

    public async Task<int> CountUploadsAsync(CancellationToken cancellationToken)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM generated_images WHERE job_id IS NULL;";
        await OpenIfNeededAsync(command.Connection, cancellationToken);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<string?> DeleteUploadAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        // job_id IS NULL is part of the predicate, so a job-backed image can never be removed through the upload path.
        command.CommandText = """
                              DELETE FROM generated_images
                              WHERE image_id = $image_id AND job_id IS NULL
                              RETURNING storage_path;
                              """;
        AddParameter(command, "$image_id", imageId);
        await OpenIfNeededAsync(command.Connection, cancellationToken);

        var storagePath = await command.ExecuteScalarAsync(cancellationToken);
        return storagePath as string;
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
