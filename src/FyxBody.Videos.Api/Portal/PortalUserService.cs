using System.Security.Claims;
using FyxBody.Videos.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FyxBody.Videos.Api.Portal;

public sealed class PortalUserService(VideosDbContext db, PortalPasswords passwords, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private static ApiException Unauthorized() => new(401, "invalid_login", "Invalid username or password.");
    private static Guid Actor(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("portal_user_id"), out var id) ? id : throw new ApiException(401, "portal_unauthorized", "Sign in to continue.");
    public static PortalUserResponse Response(PortalUser user) => new(user.Id, user.Username, user.DisplayName, user.Role, user.PartnerId, user.MustChangePassword);
    private static PortalUserAdminResponse AdminResponse(PortalUser user) => new(user.Id, user.Username, user.DisplayName, user.Role, user.PartnerId, user.MustChangePassword, user.IsActive, user.CreatedAt);

    public async Task<PortalUser?> CurrentAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var raw = principal.FindFirstValue("portal_user_id");
        if (!Guid.TryParse(raw, out var id)) return null;
        var stamp = principal.FindFirstValue("security_stamp");
        if (!Guid.TryParse(stamp, out var stampId)) return null;
        return await db.PortalUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.IsActive && u.SecurityStamp == stampId, ct);
    }

    public async Task<PortalUser> LoginAsync(PortalLoginRequest input, CancellationToken ct)
    {
        var name = PortalPasswords.Normalize(input.Username);
        var supplied = input.Password ?? "";
        if (name.Length > 128 || supplied.Length > 128)
        { passwords.VerifyDummy("invalid"); throw Unauthorized(); }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var users = await db.PortalUsers.FromSqlInterpolated($"SELECT * FROM videos.portal_users WHERE normalized_username = {name} FOR UPDATE").ToArrayAsync(ct);
        var user = users.SingleOrDefault();
        if (user == null) { passwords.VerifyDummy(supplied); throw Unauthorized(); }
        var verified = passwords.Verify(user, supplied);
        var now = Now;
        if (!user.IsActive || user.LockoutUntil > now) throw Unauthorized();
        if (verified == PasswordVerificationResult.Failed)
        {
            if (user.LockoutUntil <= now) { user.FailedLoginCount = 0; user.LockoutUntil = null; }
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= 5) { user.LockoutUntil = now.AddMinutes(15); user.FailedLoginCount = 5; }
            user.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw Unauthorized();
        }
        if (user.Role == "partner" && !await db.Partners.AsNoTracking().AnyAsync(p => p.Id == user.PartnerId && p.Status == "active", ct)) throw Unauthorized();
        if (verified == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = passwords.Hash(user, supplied);
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return user;
    }

    public async Task<PortalUser> ChangePasswordAsync(ClaimsPrincipal principal, PortalPasswordRequest input, CancellationToken ct)
    {
        PortalPasswords.Validate(input.NewPassword);
        if (input.CurrentPassword == null || input.CurrentPassword.Length > 128) throw Unauthorized();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUserAsync(Actor(principal), ct);
        if (!user.IsActive || principal.FindFirstValue("security_stamp") != user.SecurityStamp.ToString()) throw Unauthorized();
        if (passwords.Verify(user, input.CurrentPassword) == PasswordVerificationResult.Failed) throw Unauthorized();
        if (input.CurrentPassword == input.NewPassword) throw new ApiException(400, "password_unchanged", "Choose a different password.");
        user.PasswordHash = passwords.Hash(user, input.NewPassword);
        user.SecurityStamp = Guid.NewGuid();
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return user;
    }

    public async Task<PortalUserPage> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue) throw new ApiException(400, "invalid_pagination", "Invalid page or pageSize.");
        var users = await db.PortalUsers.AsNoTracking().OrderBy(x => x.Username).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(page, pageSize, await db.PortalUsers.CountAsync(ct), users.Select(AdminResponse).ToArray());
    }

    public async Task<PortalUserCreatedResponse> CreateAsync(PortalUserRequest input, CancellationToken ct)
    {
        var username = PortalPasswords.Username(input.Username);
        var displayName = PortalPasswords.DisplayName(input.DisplayName);
        if (input.Role is not ("admin" or "partner") || (input.Role == "partner") != input.PartnerId.HasValue)
            throw new ApiException(400, "invalid_role_partner", "Partner users require a partner; administrator users cannot have one.");
        var password = input.Password ?? PortalPasswords.Generate();
        PortalPasswords.Validate(password);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAdministratorsAsync(ct);
        if (input.PartnerId.HasValue && !await db.Partners.AnyAsync(p => p.Id == input.PartnerId, ct)) throw new ApiException(400, "partner_not_found", "Partner does not exist.");
        var normalized = PortalPasswords.Normalize(username);
        if (await db.PortalUsers.AnyAsync(u => u.NormalizedUsername == normalized, ct)) throw new ApiException(409, "username_exists", "Username already exists.");
        var user = new PortalUser { Username = username, NormalizedUsername = normalized, DisplayName = displayName, Role = input.Role, PartnerId = input.PartnerId, CreatedAt = Now };
        user.UpdatedAt = user.CreatedAt;
        user.PasswordHash = passwords.Hash(user, password);
        db.PortalUsers.Add(user);
        await SaveUniqueAsync(ct);
        await tx.CommitAsync(ct);
        return new(AdminResponse(user), input.Password == null ? password : null);
    }

    public async Task<PortalUserAdminResponse> UpdateAsync(ClaimsPrincipal principal, Guid id, PortalUserUpdate input, CancellationToken ct)
    {
        var name = PortalPasswords.DisplayName(input.DisplayName);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAdministratorsAsync(ct);
        var user = await LockUserAsync(id, ct);
        if (!input.IsActive)
        {
            if (Actor(principal) == id) throw new ApiException(409, "self_disable_forbidden", "You cannot disable your own account.");
            if (user.Role == "admin" && user.IsActive && !await db.PortalUsers.AnyAsync(u => u.Id != id && u.Role == "admin" && u.IsActive, ct))
                throw new ApiException(409, "last_admin", "The last active administrator cannot be disabled.");
        }
        if (user.IsActive != input.IsActive) user.SecurityStamp = Guid.NewGuid();
        user.DisplayName = name;
        user.IsActive = input.IsActive;
        user.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AdminResponse(user);
    }

    public async Task<PortalUserCreatedResponse> ResetAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUserAsync(id, ct);
        var password = PortalPasswords.Generate();
        user.PasswordHash = passwords.Hash(user, password);
        user.SecurityStamp = Guid.NewGuid();
        user.MustChangePassword = true;
        user.LockoutUntil = null;
        user.FailedLoginCount = 0;
        user.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(AdminResponse(user), password);
    }

    internal async Task LockAdministratorsAsync(CancellationToken ct) => await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1667582341)", ct);
    private async Task<PortalUser> LockUserAsync(Guid id, CancellationToken ct) => (await db.PortalUsers.FromSqlInterpolated($"SELECT * FROM videos.portal_users WHERE id = {id} FOR UPDATE").ToArrayAsync(ct)).SingleOrDefault()
        ?? throw new ApiException(404, "portal_user_not_found", "User does not exist.");
    private async Task SaveUniqueAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ApiException(409, "username_exists", "Username already exists."); }
    }
}
