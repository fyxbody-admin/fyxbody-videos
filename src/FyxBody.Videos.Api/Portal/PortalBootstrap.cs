using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;

namespace FyxBody.Videos.Api.Portal;

public static class PortalBootstrap
{
    public static async Task CreateAdminAsync(IServiceProvider services, IConfiguration configuration, CancellationToken ct)
    {
        var username = PortalPasswords.Username(configuration["PortalBootstrap:Username"]);
        var password = configuration["PortalBootstrap:Password"];
        PortalPasswords.Validate(password);
        var name = PortalPasswords.DisplayName(configuration["PortalBootstrap:DisplayName"] ?? username);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<PortalPasswords>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1667582341)", ct);
        if (await db.PortalUsers.AnyAsync(u => u.Role == "admin", ct))
            throw new InvalidOperationException("An administrator already exists; bootstrap is disabled.");
        var normalized = PortalPasswords.Normalize(username);
        if (await db.PortalUsers.AnyAsync(u => u.NormalizedUsername == normalized, ct))
            throw new InvalidOperationException("The bootstrap username is unavailable.");
        var user = new PortalUser { Username = username, NormalizedUsername = normalized, DisplayName = name, Role = "admin", MustChangePassword = true };
        user.PasswordHash = passwords.Hash(user, password!);
        db.PortalUsers.Add(user);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
