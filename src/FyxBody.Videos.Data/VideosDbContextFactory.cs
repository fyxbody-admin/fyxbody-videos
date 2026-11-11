using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace FyxBody.Videos.Data;

/// <summary>Offline design-time setup; no database connection is opened by this factory.</summary>
public sealed class VideosDbContextFactory : IDesignTimeDbContextFactory<VideosDbContext>
{
    public VideosDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Videos")
            ?? "Host=localhost;Database=fyxbody_videos;Username=design;Password=design";
        ValidateDatabase(connection);
        var options = new DbContextOptionsBuilder<VideosDbContext>()
            .UseNpgsql(connection, postgres => postgres.SetPostgresVersion(16, 0)
                .MigrationsHistoryTable("__EFMigrationsHistory", "videos"))
            .Options;
        return new VideosDbContext(options);
    }

    public static void ValidateDatabase(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.Database, "fyxbody_videos", StringComparison.Ordinal))
            throw new InvalidOperationException("The configured database must be fyxbody_videos.");
        if (builder.Host?.EndsWith(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase) == true
            && builder.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException("Azure PostgreSQL requires SSL Mode=VerifyFull.");
    }
}
