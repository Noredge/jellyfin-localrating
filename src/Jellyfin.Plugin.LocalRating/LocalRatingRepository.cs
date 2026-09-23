using Jellyfin.Plugin.LocalRating.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.LocalRating;

/// <summary>Stores private review text in the plugin's own SQLite database.</summary>
public sealed class LocalRatingRepository
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public LocalRatingRepository(IApplicationPaths applicationPaths)
    {
        var databaseDirectory = Path.Combine(applicationPaths.DataPath, "localrating");
        Directory.CreateDirectory(databaseDirectory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(databaseDirectory, "localrating.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    internal string DatabasePath => new SqliteConnectionStringBuilder(_connectionString).DataSource;

    internal async Task<ReviewRecord?> GetAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT UserId, ItemId, MediaPath, RatingSnapshot, ReviewText, CreatedAt, UpdatedAt
            FROM Reviews
            WHERE UserId = $userId AND ItemId = $itemId;
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("N"));
        command.Parameters.AddWithValue("$itemId", itemId.ToString("N"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReviewRecord(
            Guid.ParseExact(reader.GetString(0), "N"),
            Guid.ParseExact(reader.GetString(1), "N"),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture));
    }

    internal async Task<ReviewRecord> UpsertAsync(
        Guid userId,
        Guid itemId,
        string? mediaPath,
        int? ratingSnapshot,
        string reviewText,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Reviews
                (UserId, ItemId, MediaPath, RatingSnapshot, ReviewText, CreatedAt, UpdatedAt)
            VALUES
                ($userId, $itemId, $mediaPath, $rating, $review, $now, $now)
            ON CONFLICT(UserId, ItemId) DO UPDATE SET
                MediaPath = excluded.MediaPath,
                RatingSnapshot = excluded.RatingSnapshot,
                ReviewText = excluded.ReviewText,
                UpdatedAt = CASE WHEN Reviews.ReviewText <> excluded.ReviewText
                    THEN excluded.UpdatedAt ELSE Reviews.UpdatedAt END;
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("N"));
        command.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
        command.Parameters.AddWithValue("$mediaPath", (object?)mediaPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$rating", (object?)ratingSnapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("$review", reviewText);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return (await GetAsync(userId, itemId, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

            var schemaVersion = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Local Rating database schema version {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
            }

            if (schemaVersion < CurrentSchemaVersion)
            {
                await UpgradeSchemaAsync(connection, schemaVersion, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;
                """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task UpgradeSchemaAsync(
        SqliteConnection connection,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        if (schemaVersion != 0)
        {
            throw new InvalidOperationException($"No Local Rating database migration exists from schema version {schemaVersion}.");
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                CREATE TABLE IF NOT EXISTS Reviews (
                    UserId TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    MediaPath TEXT NULL,
                    RatingSnapshot INTEGER NULL CHECK (RatingSnapshot BETWEEN 1 AND 10),
                    ReviewText TEXT NOT NULL CHECK (length(ReviewText) <= 4000),
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (UserId, ItemId)
                );
                CREATE INDEX IF NOT EXISTS IX_Reviews_MediaPath ON Reviews(MediaPath);
                PRAGMA user_version = 1;
                """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
