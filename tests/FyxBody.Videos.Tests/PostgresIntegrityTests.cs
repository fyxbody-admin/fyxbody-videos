using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FyxBody.Videos.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")))
            Skip = "Set VIDEOS_TEST_CONNECTION to a disposable local PostgreSQL 16 database.";
    }
}

public sealed class PostgresIntegrityTests
{
    private static VideosDbContext CreateContext()
    {
        var connection = Environment.GetEnvironmentVariable("VIDEOS_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not ("localhost" or "127.0.0.1") || settings.Database != "fyxbody_videos")
            throw new InvalidOperationException("Integration tests require a disposable local fyxbody_videos database.");
        return new VideosDbContext(new DbContextOptionsBuilder<VideosDbContext>()
            .UseNpgsql(connection, postgres => postgres.SetPostgresVersion(16, 0)
                .MigrationsHistoryTable("__EFMigrationsHistory", "videos")).Options);
    }

    [PostgresFact]
    public async Task InitialMigrationIsAppliedAndModelHasNoPendingChanges()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [PostgresFact]
    public async Task SessionCannotUseAnotherPartnersLicense()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await SeedSession(db);
        var other = new Partner { Code = Guid.NewGuid().ToString("N"), Name = "Other" };
        db.Partners.Add(other);
        await db.SaveChangesAsync();
        session.PartnerId = other.Id;
        db.PlaybackSessions.Add(session);
        await AssertSqlState(db, PostgresErrorCodes.ForeignKeyViolation);
    }

    [PostgresFact]
    public async Task SessionCannotUseAnUnlicensedVideo()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await SeedSession(db);
        db.LicenseVideos.Remove(await db.LicenseVideos.SingleAsync(x => x.LicenseId == session.LicenseId));
        await db.SaveChangesAsync();
        db.PlaybackSessions.Add(session);
        await AssertSqlState(db, PostgresErrorCodes.ForeignKeyViolation);
    }

    [PostgresFact]
    public async Task SessionCannotUseAnAssetFromAnotherVideo()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await SeedSession(db);
        var otherVideo = new Video { ExternalExerciseId = Guid.NewGuid().ToString("N"), Title = "Other" };
        var otherAsset = new VideoAsset { VideoId = otherVideo.Id, BlobContainer = "test", BlobName = Guid.NewGuid().ToString("N") };
        db.AddRange(otherVideo, otherAsset);
        await db.SaveChangesAsync();
        session.VideoAssetId = otherAsset.Id;
        db.PlaybackSessions.Add(session);
        await AssertSqlState(db, PostgresErrorCodes.ForeignKeyViolation);
    }

    [PostgresFact]
    public async Task PartnerIdempotencyKeyCannotIssueTwoSessions()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await SeedSession(db);
        db.PlaybackSessions.Add(session);
        await db.SaveChangesAsync();
        db.Entry(session).State = EntityState.Detached;
        session.Id = Guid.NewGuid();
        db.PlaybackSessions.Add(session);
        await AssertSqlState(db, PostgresErrorCodes.UniqueViolation);
    }

    [PostgresFact]
    public async Task PlaybackEventsCannotBeCountedTwice()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await SeedSession(db);
        db.PlaybackSessions.Add(session);
        await db.SaveChangesAsync();
        var eventKey = Guid.NewGuid().ToString("N");
        db.PlaybackEvents.Add(new PlaybackEvent { PlaybackSessionId = session.Id, EventKey = eventKey, OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.PlaybackEvents.Add(new PlaybackEvent { PlaybackSessionId = session.Id, EventKey = eventKey, OccurredAt = DateTimeOffset.UtcNow });
        await AssertSqlState(db, PostgresErrorCodes.UniqueViolation);
    }

    [PostgresFact]
    public async Task NegativeQuotaIsRejected()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await SeedSession(db);
        var license = await db.Licenses.SingleAsync(x => x.Id == session.LicenseId);
        license.MonthlySessionQuota = -1;
        await AssertSqlState(db, PostgresErrorCodes.CheckViolation);
    }

    [PostgresFact]
    public async Task UsageMonthMustMatchSessionCreationMonth()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await SeedSession(db);
        session.UsageMonth = session.UsageMonth.AddMonths(-1);
        db.PlaybackSessions.Add(session);
        await AssertSqlState(db, PostgresErrorCodes.CheckViolation);
    }

    private static async Task<PlaybackSession> SeedSession(VideosDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var partner = new Partner { Code = Guid.NewGuid().ToString("N"), Name = "Test partner" };
        var video = new Video { ExternalExerciseId = Guid.NewGuid().ToString("N"), Title = "Test video" };
        var asset = new VideoAsset { VideoId = video.Id, BlobContainer = "test", BlobName = Guid.NewGuid().ToString("N"), Status = "ready" };
        var license = new License { PartnerId = partner.Id, Name = "Test license", StartsAt = now.AddDays(-1), EndsAt = now.AddDays(1), MonthlySessionQuota = 10 };
        db.AddRange(partner, video, asset, license, new LicenseVideo { LicenseId = license.Id, VideoId = video.Id });
        await db.SaveChangesAsync();
        return new PlaybackSession
        {
            PartnerId = partner.Id, LicenseId = license.Id, VideoId = video.Id, VideoAssetId = asset.Id,
            IdempotencyKey = Guid.NewGuid().ToString("N"), RequestHash = new string('a', 64),
            CreatedAt = now, ExpiresAt = now.AddMinutes(5), UsageMonth = new DateOnly(now.Year, now.Month, 1)
        };
    }

    private static async Task AssertSqlState(VideosDbContext db, string expected)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(expected, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }
}
