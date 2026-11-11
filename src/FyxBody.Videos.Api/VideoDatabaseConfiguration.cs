using Npgsql;

namespace FyxBody.Videos.Api;

public static class VideoDatabaseConfiguration
{
    public static string? GetConnectionString(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("Videos");
        if (string.IsNullOrWhiteSpace(value)) return null;
        var connection = new NpgsqlConnectionStringBuilder(value);
        if (!string.Equals(connection.Database, "fyxbody_videos", StringComparison.Ordinal))
            throw new InvalidOperationException("This service must use the fyxbody_videos database.");
        if (connection.Host?.EndsWith(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase) == true
            && connection.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException("Azure PostgreSQL requires SSL Mode=VerifyFull.");
        connection.IncludeErrorDetail = false;
        connection.LogParameters = false;
        return connection.ConnectionString;
    }
}
