using Jellyfin.Plugin.LocalRating.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.LocalRating.Tests;

public sealed class LocalRatingRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Jellyfin.Plugin.LocalRating.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpsertAsync_RoundTripsAndPreservesCreatedAt()
    {
        var repository = CreateRepository();
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var created = await repository.UpsertAsync(
            userId,
            itemId,
            @"D:\Media\movie.mp4",
            8,
            "First review",
            TestContext.Current.CancellationToken);
        await Task.Delay(10, TestContext.Current.CancellationToken);
        var updated = await repository.UpsertAsync(
            userId,
            itemId,
            @"D:\Media\renamed.mp4",
            9,
            "Updated review",
            TestContext.Current.CancellationToken);
        var loaded = await repository.GetAsync(userId, itemId, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(userId, loaded.UserId);
        Assert.Equal(itemId, loaded.ItemId);
        Assert.Equal(@"D:\Media\renamed.mp4", loaded.MediaPath);
        Assert.Equal(9, loaded.RatingSnapshot);
        Assert.Equal("Updated review", loaded.ReviewText);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
        Assert.True(File.Exists(repository.DatabasePath));
    }

    [Fact]
    public async Task GetAsync_IsolatesRecordsByUserAndItem()
    {
        var repository = CreateRepository();
        var itemId = Guid.NewGuid();
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();

        await repository.UpsertAsync(firstUser, itemId, null, 6, "Private A", TestContext.Current.CancellationToken);
        await repository.UpsertAsync(secondUser, itemId, null, 10, "Private B", TestContext.Current.CancellationToken);

        var first = await repository.GetAsync(firstUser, itemId, TestContext.Current.CancellationToken);
        var second = await repository.GetAsync(secondUser, itemId, TestContext.Current.CancellationToken);
        var missing = await repository.GetAsync(Guid.NewGuid(), itemId, TestContext.Current.CancellationToken);

        Assert.Equal("Private A", first?.ReviewText);
        Assert.Equal(6, first?.RatingSnapshot);
        Assert.Equal("Private B", second?.ReviewText);
        Assert.Equal(10, second?.RatingSnapshot);
        Assert.Null(missing);
    }

    [Fact]
    public async Task UpsertAsync_OnlyTextChangesAdvanceReviewTimestamp()
    {
        var repository = CreateRepository();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var created = await repository.UpsertAsync(user, item, null, 6, "Review", token);
        await Task.Delay(20, token);
        var unchanged = await repository.UpsertAsync(user, item, "new-path", 8, "Review", token);
        Assert.Equal(created.CreatedAt, unchanged.CreatedAt);
        Assert.Equal(created.UpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(8, unchanged.RatingSnapshot);
        Assert.Equal("new-path", unchanged.MediaPath);
        var edited = await repository.UpsertAsync(user, item, "new-path", 8, "Review edited", token);
        Assert.Equal(created.CreatedAt, edited.CreatedAt);
        Assert.True(edited.UpdatedAt > unchanged.UpdatedAt);
        var cleared = await repository.UpsertAsync(user, item, "new-path", 8, string.Empty, token);
        Assert.True(cleared.UpdatedAt >= edited.UpdatedAt);
    }

    [Fact]
    public async Task UpsertAsync_RejectsDatabaseConstraintViolations()
    {
        var repository = CreateRepository();

        await Assert.ThrowsAsync<SqliteException>(() => repository.UpsertAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            11,
            string.Empty,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => repository.UpsertAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            8,
            new string('x', 4001),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpsertAsync_InitializesOnceUnderConcurrentFirstUse()
    {
        var repository = CreateRepository();
        var userId = Guid.NewGuid();
        var itemIds = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(itemIds.Select((itemId, index) => repository.UpsertAsync(
            userId,
            itemId,
            null,
            index + 1,
            $"Review {index + 1}",
            TestContext.Current.CancellationToken)));

        foreach (var itemId in itemIds)
        {
            Assert.NotNull(await repository.GetAsync(userId, itemId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task UpsertAsync_InitializesDatabaseAtSchemaVersionOne()
    {
        var repository = CreateRepository();

        await repository.UpsertAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            8,
            "Versioned review",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await ReadSchemaVersionAsync(repository.DatabasePath));
    }

    [Fact]
    public async Task GetAsync_UpgradesUnversionedDatabaseWithoutLosingReview()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var repository = CreateRepository();
        await CreateLegacyDatabaseAsync(repository.DatabasePath, userId, itemId);

        var review = await repository.GetAsync(userId, itemId, TestContext.Current.CancellationToken);

        Assert.NotNull(review);
        Assert.Equal("Existing review", review.ReviewText);
        Assert.Equal(7, review.RatingSnapshot);
        Assert.Equal(1, await ReadSchemaVersionAsync(repository.DatabasePath));
    }

    [Fact]
    public async Task GetAsync_RejectsDatabaseFromNewerPluginVersion()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(repository.DatabasePath)!);
        await using (var connection = new SqliteConnection($"Data Source={repository.DatabasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken));

        Assert.Contains("newer than supported version 1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, await ReadSchemaVersionAsync(repository.DatabasePath));
    }

    [Fact]
    public async Task StoppedDatabaseFolderCopy_RestoresExistingReview()
    {
        var sourceRepository = CreateRepository();
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await sourceRepository.UpsertAsync(
            userId,
            itemId,
            @"D:\Media\backup-test.mp4",
            9,
            "Review from backup",
            TestContext.Current.CancellationToken);
        SqliteConnection.ClearAllPools();

        var restoredRoot = Path.Combine(_root, "restored");
        var restoredDirectory = Path.Combine(restoredRoot, "data", "localrating");
        Directory.CreateDirectory(restoredDirectory);
        foreach (var sourceFile in Directory.GetFiles(Path.GetDirectoryName(sourceRepository.DatabasePath)!))
        {
            File.Copy(sourceFile, Path.Combine(restoredDirectory, Path.GetFileName(sourceFile)));
        }

        var restoredRepository = new LocalRatingRepository(new TestApplicationPaths(restoredRoot));
        var restored = await restoredRepository.GetAsync(userId, itemId, TestContext.Current.CancellationToken);

        Assert.NotNull(restored);
        Assert.Equal("Review from backup", restored.ReviewText);
        Assert.Equal(9, restored.RatingSnapshot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalRatingRepository CreateRepository() => new(new TestApplicationPaths(_root));

    private static async Task<int> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task CreateLegacyDatabaseAsync(string databasePath, Guid userId, Guid itemId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Reviews (
                UserId TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                MediaPath TEXT NULL,
                RatingSnapshot INTEGER NULL CHECK (RatingSnapshot BETWEEN 1 AND 10),
                ReviewText TEXT NOT NULL CHECK (length(ReviewText) <= 4000),
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (UserId, ItemId)
            );
            CREATE INDEX IX_Reviews_MediaPath ON Reviews(MediaPath);
            INSERT INTO Reviews
                (UserId, ItemId, MediaPath, RatingSnapshot, ReviewText, CreatedAt, UpdatedAt)
            VALUES
                ($userId, $itemId, NULL, 7, 'Existing review', $now, $now);
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("N"));
        command.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class TestApplicationPaths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;
        public string WebPath => Path.Combine(root, "web");
        public string ProgramSystemPath => Path.Combine(root, "system");
        public string DataPath => Path.Combine(root, "data");
        public string ImageCachePath => Path.Combine(root, "images");
        public string PluginsPath => Path.Combine(root, "plugins");
        public string PluginConfigurationsPath => Path.Combine(root, "plugin-configurations");
        public string LogDirectoryPath => Path.Combine(root, "logs");
        public string ConfigurationDirectoryPath => Path.Combine(root, "config");
        public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");
        public string CachePath => Path.Combine(root, "cache");
        public string TempDirectory => Path.Combine(root, "temp");
        public string VirtualDataPath => Path.Combine(root, "virtual-data");
        public string TrickplayPath => Path.Combine(root, "trickplay");
        public string BackupPath => Path.Combine(root, "backups");

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive)
        {
        }
    }
}
