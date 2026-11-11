using FyxBody.Videos.Api.Admin;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PartnerEntity = FyxBody.Videos.Data.Partner;

namespace FyxBody.Videos.Api.Partner;

public sealed record SubpartnerInput(string Code, string Name, string Status);
public sealed record DelegatedCatalogueInput(Guid SourceLicenseId, Guid[]? VideoIds, bool AllCurrent = false);

public static class PartnerDelegationEndpoints
{
    public static IEndpointRouteBuilder MapPartnerDelegationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/subpartners").RequireAuthorization("partner").RequireRateLimiting("partner")
            .WithGroupName("partner").WithTags("Partner delegation");
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{subpartnerId:guid}", Get);
        group.MapPut("/{subpartnerId:guid}", Update);
        group.MapGet("/{subpartnerId:guid}/credentials", ListCredentials);
        group.MapPost("/{subpartnerId:guid}/credentials", CreateCredential);
        group.MapDelete("/{subpartnerId:guid}/credentials/{credentialId:guid}", RevokeCredential);
        group.MapGet("/{subpartnerId:guid}/licenses", ListLicenses);
        group.MapPost("/{subpartnerId:guid}/licenses", CreateLicense);
        group.MapGet("/{subpartnerId:guid}/licenses/{licenseId:guid}", GetLicense);
        group.MapPut("/{subpartnerId:guid}/licenses/{licenseId:guid}", UpdateLicense);
        group.MapGet("/{subpartnerId:guid}/licenses/{licenseId:guid}/catalogue", ReadCatalogue);
        group.MapPut("/{subpartnerId:guid}/licenses/{licenseId:guid}/catalogue", ReplaceCatalogue)
            .WithSummary("Replace delegated grants from a principal license")
            .WithDescription("AllCurrent takes a snapshot of ready videos from the source license. Existing owner uploads and grants from other sources are preserved.");
        return endpoints;
    }

    private static async Task<AdminPage<PartnerView>> List(HttpContext context, VideosDbContext db, PartnerVideoService videos,
        TimeProvider clock, int? page, int? pageSize, CancellationToken ct)
    {
        var id = await Authorize(context, db, videos, clock, ct);
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        var query = db.Partners.AsNoTracking().Where(x => x.ParentPartnerId == id);
        var rows = await query.OrderBy(x => x.Code).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new(rows.Select(View).ToArray(), p, size, await query.CountAsync(ct));
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<PartnerView>> Create(SubpartnerInput input,
        HttpContext context, VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        var valid = AdminValidation.Partner(new(input.Code, input.Name, input.Status));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = await Authorize(context, db, videos, clock, ct, lockPrincipal: true);
        var child = new PartnerEntity { Code = valid.Code, Name = valid.Name, Status = valid.Status, ParentPartnerId = id, CreatedAt = clock.GetUtcNow() };
        db.Partners.Add(child);
        await Save(db, ct); await tx.CommitAsync(ct);
        return TypedResults.Created($"/v1/subpartners/{child.Id}", View(child));
    }

    private static async Task<PartnerView> Get(Guid subpartnerId, HttpContext context, VideosDbContext db,
        PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        var id = await Authorize(context, db, videos, clock, ct);
        return View(await Child(db, id, subpartnerId, ct));
    }

    private static async Task<AdminPage<CredentialView>> ListCredentials(Guid subpartnerId, HttpContext context, VideosDbContext db,
        PartnerVideoService videos, TimeProvider clock, int? page, int? pageSize, CancellationToken ct)
    {
        var id = await Authorize(context, db, videos, clock, ct);
        await Child(db, id, subpartnerId, ct);
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        var query = db.PartnerCredentials.AsNoTracking().Where(x => x.PartnerId == subpartnerId);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new(rows.Select(CredentialViewOf).ToArray(), p, size, await query.CountAsync(ct));
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<CreatedCredentialView>> CreateCredential(Guid subpartnerId,
        CredentialInput input, HttpContext context, VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (input.ExpiresAt is { } expires && (expires.Offset != TimeSpan.Zero || expires <= now))
            throw new ApiException(400, "invalid_request", "expiresAt must be a future UTC timestamp.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = await Authorize(context, db, videos, clock, ct, lockPrincipal: true);
        await Child(db, id, subpartnerId, ct, locked: true);
        var (keyId, secret, hash) = CredentialSecrets.Generate();
        var credential = new PartnerCredential { PartnerId = subpartnerId, KeyId = keyId, SecretHash = hash, CreatedAt = now, ExpiresAt = input.ExpiresAt };
        db.PartnerCredentials.Add(credential);
        await Save(db, ct); await tx.CommitAsync(ct);
        return TypedResults.Created($"/v1/subpartners/{subpartnerId}/credentials", new CreatedCredentialView(CredentialViewOf(credential), $"{keyId}.{secret}"));
    }

    private static async Task<CredentialView> RevokeCredential(Guid subpartnerId, Guid credentialId, HttpContext context,
        VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = await Authorize(context, db, videos, clock, ct, lockPrincipal: true);
        await Child(db, id, subpartnerId, ct, locked: true);
        var credential = await db.PartnerCredentials.SingleOrDefaultAsync(x => x.Id == credentialId && x.PartnerId == subpartnerId, ct)
            ?? throw new ApiException(404, "not_found", "Subpartner credential is unavailable.");
        credential.RevokedAt ??= clock.GetUtcNow();
        await Save(db, ct); await tx.CommitAsync(ct);
        return CredentialViewOf(credential);
    }

    private static async Task<PartnerView> Update(Guid subpartnerId, SubpartnerInput input,
        HttpContext context, VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        var valid = AdminValidation.Partner(new(input.Code, input.Name, input.Status));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = await Authorize(context, db, videos, clock, ct, lockPrincipal: true);
        var child = await Child(db, id, subpartnerId, ct, locked: true);
        child.Code = valid.Code; child.Name = valid.Name; child.Status = valid.Status;
        await Save(db, ct); await tx.CommitAsync(ct);
        return View(child);
    }

    private static async Task<AdminPage<LicenseView>> ListLicenses(Guid subpartnerId, HttpContext context, VideosDbContext db,
        PartnerVideoService videos, TimeProvider clock, int? page, int? pageSize, CancellationToken ct)
    {
        var id = await Authorize(context, db, videos, clock, ct);
        await Child(db, id, subpartnerId, ct);
        var (p, size) = AdminValidation.Pagination(page, pageSize);
        var query = db.Licenses.AsNoTracking().Where(x => x.PartnerId == subpartnerId);
        var rows = await query.OrderBy(x => x.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new(rows.Select(LicenseViewOf).ToArray(), p, size, await query.CountAsync(ct));
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Created<LicenseView>> CreateLicense(Guid subpartnerId, LicenseInput input,
        HttpContext context, VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = await Authorize(context, db, videos, clock, ct, lockPrincipal: true);
        await Child(db, id, subpartnerId, ct, locked: true);
        var license = new License { PartnerId = subpartnerId, CreatedAt = clock.GetUtcNow() };
        await RequireUploadDelegation(db, id, clock, input, ct);
        ApplyChildLicense(input, license);
        db.Licenses.Add(license);
        await Save(db, ct); await tx.CommitAsync(ct);
        return TypedResults.Created($"/v1/subpartners/{subpartnerId}/licenses/{license.Id}", LicenseViewOf(license));
    }

    private static async Task<LicenseView> GetLicense(Guid subpartnerId, Guid licenseId, HttpContext context,
        VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        var id = await Authorize(context, db, videos, clock, ct);
        await Child(db, id, subpartnerId, ct);
        return LicenseViewOf(await FindLicense(db, subpartnerId, licenseId, ct));
    }

    private static async Task<LicenseView> UpdateLicense(Guid subpartnerId, Guid licenseId, LicenseInput input,
        HttpContext context, VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = await Authorize(context, db, videos, clock, ct, lockPrincipal: true);
        await Child(db, id, subpartnerId, ct, locked: true);
        var license = await FindLicense(db, subpartnerId, licenseId, ct);
        await RequireUploadDelegation(db, id, clock, input, ct);
        ApplyChildLicense(input, license);
        await Save(db, ct); await tx.CommitAsync(ct);
        return LicenseViewOf(license);
    }

    private static async Task<CatalogueView> ReadCatalogue(Guid subpartnerId, Guid licenseId, Guid sourceLicenseId, HttpContext context,
        VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        var id = await Authorize(context, db, videos, clock, ct);
        await Child(db, id, subpartnerId, ct);
        await FindLicense(db, subpartnerId, licenseId, ct);
        if (!await db.Licenses.AnyAsync(x => x.Id == sourceLicenseId && x.PartnerId == id, ct))
            throw new ApiException(404, "not_found", "Source license is unavailable.");
        return await ReadDelegated(db, licenseId, sourceLicenseId, ct);
    }

    private static async Task<CatalogueView> ReplaceCatalogue(Guid subpartnerId, Guid licenseId, DelegatedCatalogueInput input,
        HttpContext context, VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct)
    {
        if (input.AllCurrent && input.VideoIds is { Length: > 0 })
            throw new ApiException(400, "invalid_request", "Use allCurrent or videoIds.");
        var selected = input.AllCurrent ? [] : AdminValidation.Catalogue(input.VideoIds);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = await Authorize(context, db, videos, clock, ct, lockPrincipal: true);
        await Child(db, id, subpartnerId, ct, locked: true);
        await FindLicense(db, subpartnerId, licenseId, ct);
        var now = clock.GetUtcNow();
        if (!await CatalogueAccess.ActiveLicenses(db, id, now).AnyAsync(x => x.Id == input.SourceLicenseId && x.AllowCatalogueDelegation, ct))
            throw new ApiException(403, "catalogue_delegation_forbidden", "The source license does not allow catalogue delegation.");
        var eligible = CatalogueAccess.DelegableVideos(db, id, input.SourceLicenseId);
        var requested = (await (input.AllCurrent ? eligible : eligible.Where(x => selected.Contains(x.Id))).Select(x => x.Id).ToArrayAsync(ct)).ToHashSet();
        if (!input.AllCurrent && requested.Count != selected.Length)
            throw new ApiException(403, "catalogue_delegation_forbidden", "A requested video is outside the source catalogue.");
        var existing = await db.LicenseVideos.Where(x => x.LicenseId == licenseId).ToListAsync(ct);
        foreach (var grant in existing)
        {
            if (requested.Remove(grant.VideoId))
            {
                if (grant.RevokedAt == null && grant.SourceLicenseId != input.SourceLicenseId) continue;
                grant.SourceLicenseId = input.SourceLicenseId;
                grant.RevokedAt = null;
            }
            else if (grant.SourceLicenseId == input.SourceLicenseId)
                grant.RevokedAt ??= now;
        }
        db.LicenseVideos.AddRange(requested.Select(videoId => new LicenseVideo
        {
            LicenseId = licenseId, VideoId = videoId, SourceLicenseId = input.SourceLicenseId, CreatedAt = now
        }));
        await Save(db, ct); await tx.CommitAsync(ct);
        return await ReadDelegated(db, licenseId, input.SourceLicenseId, ct);
    }

    private static async Task<CatalogueView> ReadDelegated(VideosDbContext db, Guid licenseId, Guid sourceLicenseId, CancellationToken ct) =>
        new(await db.LicenseVideos.AsNoTracking().Where(x => x.LicenseId == licenseId && x.SourceLicenseId == sourceLicenseId && x.RevokedAt == null)
            .OrderBy(x => x.VideoId).Select(x => x.VideoId).ToArrayAsync(ct));

    private static async Task RequireUploadDelegation(VideosDbContext db, Guid id, TimeProvider clock, LicenseInput input, CancellationToken ct)
    {
        if ((input.AllowPartnerUploads == true || input.UploadQuotaMode != null)
            && !await CatalogueAccess.ActiveLicenses(db, id, clock.GetUtcNow()).AnyAsync(x => x.AllowSubpartnerManagement && x.AllowPartnerUploads, ct))
            throw new ApiException(403, "upload_delegation_forbidden", "The principal cannot configure private uploads for its subpartners.");
    }

    private static async Task<Guid> Authorize(HttpContext context, VideosDbContext db, PartnerVideoService videos, TimeProvider clock, CancellationToken ct, bool lockPrincipal = false)
    {
        var identity = PartnerIdentity.From(context.User);
        var query = lockPrincipal
            ? db.Partners.FromSqlInterpolated($"SELECT * FROM videos.partners WHERE id = {identity.PartnerId} FOR UPDATE")
            : db.Partners.Where(x => x.Id == identity.PartnerId);
        var principal = await query.AsNoTracking().SingleOrDefaultAsync(ct);
        await videos.ValidateIdentityAsync(identity, ct);
        if (principal is null || principal.ParentPartnerId != null || !await CatalogueAccess.ActiveLicenses(db, identity.PartnerId, clock.GetUtcNow())
            .AnyAsync(x => x.AllowSubpartnerManagement, ct))
            throw new ApiException(403, "subpartner_management_forbidden", "The principal does not have an active license allowing subpartner management.");
        return principal.Id;
    }

    private static async Task<PartnerEntity> Child(VideosDbContext db, Guid principalId, Guid childId, CancellationToken ct, bool locked = false)
    {
        var query = locked
            ? db.Partners.FromSqlInterpolated($"SELECT * FROM videos.partners WHERE id = {childId} AND parent_partner_id = {principalId} FOR UPDATE")
            : db.Partners.Where(x => x.Id == childId && x.ParentPartnerId == principalId);
        return await query.SingleOrDefaultAsync(ct) ?? throw new ApiException(404, "not_found", "Subpartner is unavailable.");
    }

    private static async Task<License> FindLicense(VideosDbContext db, Guid childId, Guid licenseId, CancellationToken ct) =>
        await db.Licenses.SingleOrDefaultAsync(x => x.Id == licenseId && x.PartnerId == childId, ct)
        ?? throw new ApiException(404, "not_found", "Subpartner license is unavailable.");

    private static void ApplyChildLicense(LicenseInput input, License license)
    {
        if (input.AllowSubpartnerManagement == true || input.AllowCatalogueDelegation == true)
            throw new ApiException(400, "invalid_request", "Subpartners cannot manage or delegate to another level.");
        ApplyLicense(AdminValidation.License(input), license);
    }

    internal static void ApplyLicense(LicenseInput input, License license)
    {
        license.Name = input.Name; license.Status = input.Status; license.StartsAt = input.StartsAt; license.EndsAt = input.EndsAt;
        license.UnlimitedSessions = input.UnlimitedSessions ?? license.UnlimitedSessions;
        license.MonthlySessionQuota = license.UnlimitedSessions ? 0 : input.MonthlySessionQuota;
        license.AllowSubpartnerManagement = input.AllowSubpartnerManagement ?? license.AllowSubpartnerManagement;
        license.AllowCatalogueDelegation = input.AllowCatalogueDelegation ?? license.AllowCatalogueDelegation;
        if (!license.AllowPartnerUploads && input.AllowPartnerUploads == true && input.UploadQuotaMode is null)
            throw new ApiException(400, "invalid_request", "Choose the upload quota mode when enabling uploads.");
        license.AllowPartnerUploads = input.AllowPartnerUploads ?? license.AllowPartnerUploads;
        if (input.UploadQuotaMode is { } mode)
        {
            if ((mode is "storage" or "videos" && input.UploadQuotaLimit is not > 0)
                || (mode == "unlimited" && input.UploadQuotaLimit is not null)
                || mode is not ("storage" or "videos" or "unlimited"))
                throw new ApiException(400, "invalid_request", "Set uploadQuotaMode to storage (bytes), videos or unlimited and supply a positive limit for a limited mode.");
            license.UploadQuotaMode = mode;
            license.UploadQuotaLimit = input.UploadQuotaLimit;
        }
        else if (input.UploadQuotaLimit != null)
            throw new ApiException(400, "invalid_request", "A quota limit requires uploadQuotaMode.");
    }

    private static CredentialView CredentialViewOf(PartnerCredential x) => new(x.Id, x.KeyId, x.CreatedAt, x.ExpiresAt, x.RevokedAt);
    internal static PartnerView View(PartnerEntity x) => new(x.Id, x.Code, x.Name, x.Status, x.CreatedAt, x.ParentPartnerId);
    internal static LicenseView LicenseViewOf(License x) => new(x.Id, x.PartnerId, x.Name, x.Status, x.StartsAt, x.EndsAt,
        x.MonthlySessionQuota, x.CreatedAt, x.UnlimitedSessions, x.AllowSubpartnerManagement, x.AllowCatalogueDelegation,
        x.AllowPartnerUploads, x.UploadQuotaMode, x.UploadQuotaLimit);

    private static async Task Save(VideosDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ApiException(409, "resource_conflict", "A matching resource already exists."); }
    }
}
